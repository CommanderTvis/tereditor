using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.ClaudeCode.UI;

/// <summary>What the capabilities surface asks of the pane: open a source, review an edit, run one of Claude's commands, and keep which parents are folded.</summary>
internal interface IClaudeCapabilityHost
{
    /// <summary>The project the pane describes, or null when none is open.</summary>
    string? ProjectRoot { get; }
    void Open(string path, IReadOnlyList<string>? keyPath);
    void Review(PendingEdit pending);
    void Uninstall(PluginUninstallTarget target);
    void Move(PluginMoveTarget target);
    void MoveSkill(SkillMoveTarget target);
    void Marketplace(ClaudeMarketplaceAction action);
    void Add(string kind);
    bool IsCollapsed(string key);
    void SetCollapsed(string key, bool collapsed);
}

/// <summary>A row that folds: its key, whether anything hangs under it, and whether it starts open (a marketplace does; a plugin, with its many parts, does not).</summary>
internal sealed record ClaudeFold(string Key, bool HasChildren, bool DefaultOpen);

/// <summary>A marketplace with the plugins installed from it; a null marketplace is the group of plugins whose marketplace is not listed.</summary>
internal sealed record ClaudePluginGroup(ClaudeCapability? Marketplace, IReadOnlyList<ClaudeCapability> Plugins)
{
    public string Key => "plugins/" + (Marketplace?.Name ?? "\0none");
    public string Label => Marketplace?.Name ?? "No marketplace";
    public ClaudeFold Fold => new(Key, Plugins.Count > 0, true);
}

/// <summary>One entry of a row's menu.</summary>
internal sealed record ClaudeCapabilityAction(string Name, string Label, Action Run, string? Tip = null);

/// <summary>What Claude can reach: MCP servers, plugins under their marketplaces, skills, subagents and hooks, each with its source and switches.</summary>
internal sealed class ClaudeCapabilitiesSurface(IClaudeCapabilityHost pane, IReadOnlyList<ClaudeCapability> capabilities)
{
    private static readonly (ClaudeCapabilityKind Kind, string Heading)[] Sections =
    [
        (ClaudeCapabilityKind.Mcp, "MCP servers"), (ClaudeCapabilityKind.Plugin, "Plugins"), (ClaudeCapabilityKind.Skill, "Skills"),
        (ClaudeCapabilityKind.Agent, "Subagents"), (ClaudeCapabilityKind.Hook, "Hooks")
    ];

    private sealed class Folding(Avalonia.Controls.Shapes.Path chevron, Control? stem, ClaudeFold fold)
    {
        public ClaudeFold Fold { get; } = fold;
        public Avalonia.Controls.Shapes.Path Chevron { get; } = chevron;
        public Control? Stem { get; } = stem;
        public Control? Parts { get; set; }
    }

    private readonly Dictionary<string, Folding> folding = [];

    /// <summary>
    /// Marketplaces in the order they are listed, each with the plugins named <c>plugin@marketplace</c> after it, then the
    /// plugins of no listed marketplace (removed, or inline) as one last group, so no plugin is lost.
    /// </summary>
    public static IReadOnlyList<ClaudePluginGroup> PluginGroups(IReadOnlyList<ClaudeCapability> capabilities)
    {
        var plugins = capabilities.Where(item => item.Kind == ClaudeCapabilityKind.Plugin).ToArray();
        var marketplaces = capabilities.Where(item => item.Kind == ClaudeCapabilityKind.Marketplace).ToArray();
        var groups = marketplaces.Select(market => new ClaudePluginGroup(market, [.. plugins.Where(plugin => plugin.Detail == market.Name)])).ToList();
        var loose = plugins.Where(plugin => marketplaces.All(market => market.Name != plugin.Detail)).ToArray();
        if (loose.Length > 0) groups.Add(new(null, loose));
        return groups;
    }

    private static readonly (string Kind, string Label)[] Addable = [("mcp", "MCP server"), ("skill", "Skill"), ("hook", "Hook"), ("plugin", "Plugin"), ("marketplace", "Marketplace")];

    /// <summary>The edit a row's switch proposes, or null for a capability Claude Code gives no switch.</summary>
    private static PendingEdit? ToggleFor(ClaudeCapability item) => item.Kind switch
    {
        // A server no file declares (a claude.ai connector, a plugin's) is switched in /mcp, not here.
        ClaudeCapabilityKind.Mcp when item.Origin.Path is null => null,
        ClaudeCapabilityKind.Mcp => new(new McpEdit(item.Name, !item.Enabled), item.Enabled ? $"Deny \"{item.Name}\"" : $"Allow \"{item.Name}\""),
        ClaudeCapabilityKind.Plugin => new(new PluginEdit(item.Name, !item.Enabled), item.Enabled ? $"Turn off \"{item.Name}\"" : $"Turn on \"{item.Name}\""),
        ClaudeCapabilityKind.Skill => new(new SkillEdit(item.Name, !item.Enabled), item.Enabled ? $"Turn off \"{item.Name}\"" : $"Turn on \"{item.Name}\""),
        _ => null
    };

    // Uninstalling and marketplace runs are Claude's own CLI against a scope it accepts; managed settings have none.
    private static ClaudeWritableScope? WritableScope(ClaudeCapability item) =>
        ClaudeValues.WritableScopes.Cast<ClaudeWritableScope?>().FirstOrDefault(scope => scope!.Value.Name() == item.Origin.Scope.Name());

    public Control View
    {
        get
        {
            var list = new StackPanel();
            var adding = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4) };
            adding.Children.Add(Ui.Text("WHAT CLAUDE CAN REACH", Ui.Muted, 11));
            var buttons = new WrapPanel();
            foreach (var (kind, label) in Addable)
            {
                var button = Ui.Button(label, () =>
                {
                    if (kind == "marketplace") pane.Marketplace(new AddMarketplace("", ClaudeWritableScope.User));
                    else pane.Add(kind);
                }, "add");
                button.Name = "ClaudeAdd_" + kind;
                button.Margin = new Thickness(0, 0, 4, 4);
                buttons.Children.Add(button);
            }
            adding.Children.Add(buttons);
            list.Children.Add(new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = adding });
            list.Children.Add(new ClaudeLspCapability());
            if (capabilities.Count == 0)
            {
                var none = Ui.Text("No additional capabilities are configured here.", Ui.Muted, 13);
                none.Margin = new Thickness(8);
                list.Children.Add(none);
            }
            folding.Clear();
            foreach (var (kind, heading) in Sections)
            {
                var rows = capabilities.Where(item => item.Kind == kind).ToArray();
                var groups = kind == ClaudeCapabilityKind.Plugin ? PluginGroups(capabilities) : [];
                if (rows.Length == 0 && groups.Count == 0) continue;
                var section = new StackPanel { Name = "ClaudeCapabilitySection", Tag = kind };
                section.Children.Add(new Border { Background = Ui.Header, Padding = new Thickness(8, 2), Child = Ui.Text(heading.ToUpperInvariant(), Ui.Muted, 11) });
                if (kind == ClaudeCapabilityKind.Plugin) AddTree(section, groups);
                else foreach (var item in rows) section.Children.Add(Row(item));
                list.Children.Add(section);
            }
            return list;
        }
    }

    // A marketplace is the parent row; its plugins hang under it by the same connectors an @-import hangs under its file.
    private void AddTree(StackPanel section, IReadOnlyList<ClaudePluginGroup> groups)
    {
        foreach (var group in groups)
        {
            section.Children.Add(group.Marketplace is { } market ? Row(market, group.Fold) : NoMarketplaceRow(group));
            if (group.Plugins.Count == 0) continue;
            var parts = new StackPanel { Name = "ClaudeCapabilityChildren", Tag = group.Key, IsVisible = IsOpen(group.Fold) };
            for (var index = 0; index < group.Plugins.Count; index++)
            {
                var plugin = group.Plugins[index];
                var last = index == group.Plugins.Count - 1;
                var fold = plugin.Parts is { Count: > 0 } ? new ClaudeFold("plugin/" + plugin.Name, true, false) : null;
                parts.Children.Add(Child(Row(plugin, fold, underMarketplace: group.Marketplace is not null), 1, last));
                if (fold is null) continue;
                var inner = PluginPartsPanel(plugin, fold, last);
                folding[fold.Key].Parts = inner;
                parts.Children.Add(inner);
            }
            folding[group.Key].Parts = parts;
            section.Children.Add(parts);
        }
    }

    // What a folded row stands for: a plugin's panel is shut until the user opens it, a marketplace's is open until the user shuts it.
    private bool IsOpen(ClaudeFold fold) => fold.DefaultOpen ? !pane.IsCollapsed(fold.Key) : pane.IsCollapsed(fold.Key);

    private static readonly (ClaudePluginPartKind Kind, string Heading, string One, string Many)[] PartKinds =
    [
        (ClaudePluginPartKind.Skill, "Skills", "skill", "skills"), (ClaudePluginPartKind.Command, "Commands", "command", "commands"),
        (ClaudePluginPartKind.Agent, "Agents", "agent", "agents"), (ClaudePluginPartKind.Hook, "Hooks", "hook", "hooks"),
        (ClaudePluginPartKind.Mcp, "MCP servers", "MCP server", "MCP servers"), (ClaudePluginPartKind.Lsp, "LSP servers", "LSP server", "LSP servers"),
        (ClaudePluginPartKind.OutputStyle, "Output styles", "output style", "output styles"), (ClaudePluginPartKind.Workflow, "Workflows", "workflow", "workflows"),
        (ClaudePluginPartKind.Theme, "Themes", "theme", "themes"), (ClaudePluginPartKind.Monitor, "Monitors", "monitor", "monitors")
    ];

    /// <summary>The count line of a plugin row: "12 skills, 3 agents", or why nothing is listed.</summary>
    internal static string PartsSummary(ClaudeCapability plugin)
    {
        if (plugin.Parts is null) return "Files not found";
        if (plugin.Parts.Count == 0) return "Adds nothing";
        return string.Join(", ", PartKinds.Select(kind => (kind, count: plugin.Parts.Count(part => part.Kind == kind.Kind))).Where(item => item.count > 0)
            .Select(item => $"{item.count} {(item.count == 1 ? item.kind.One : item.kind.Many)}"));
    }

    // The parts of one plugin, grouped by kind: a heading row per kind under the plugin, the parts under the heading.
    private StackPanel PluginPartsPanel(ClaudeCapability plugin, ClaudeFold fold, bool pluginLast)
    {
        var panel = new StackPanel { Name = "ClaudeCapabilityChildren", Tag = fold.Key, IsVisible = IsOpen(fold), Opacity = plugin.Enabled ? 1 : 0.55 };
        var kinds = PartKinds.Select(kind => (kind, parts: plugin.Parts!.Where(part => part.Kind == kind.Kind).ToArray())).Where(item => item.parts.Length > 0).ToArray();
        for (var index = 0; index < kinds.Length; index++)
        {
            var (kind, parts) = kinds[index];
            var headingLast = index == kinds.Length - 1;
            var heading = Ui.Text($"{kind.Heading} · {parts.Length}", Ui.Muted, 11);
            heading.Name = "ClaudePluginKind";
            heading.Tag = kind.Kind;
            heading.Margin = new Thickness(8, 4);
            panel.Children.Add(Child(heading, 2, headingLast, [pluginLast]));
            for (var number = 0; number < parts.Length; number++)
                panel.Children.Add(Child(PartRow(parts[number]), 3, number == parts.Length - 1, [pluginLast, headingLast]));
        }
        return panel;
    }

    // One thing a plugin adds: its name as Claude Code shows it, what it is for, and the file that declares it.
    private Control PartRow(ClaudePluginPart part)
    {
        var row = new StackPanel { Name = "ClaudePluginPart", Tag = part, Margin = new Thickness(8, 3) };
        row.Children.Add(Ui.Text(part.Name, Ui.TextBrush, 13));
        if (part.Description is { Length: > 0 } description) row.Children.Add(ClaudeParts.Wrapped(description, Ui.Hint, 11));
        if (part.Path is { } path) row.Children.Add(ClaudeParts.SourceButton(path, null, pane.Open));
        Avalonia.Automation.AutomationProperties.SetName(row, part.Name);
        if (part.Description is not null) Avalonia.Automation.AutomationProperties.SetHelpText(row, part.Description);
        return new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }

    private static Control Child(Control content, int depth, bool last, IReadOnlyList<bool>? endedAbove = null)
    {
        var line = new DockPanel();
        foreach (var guide in ClaudeTree.Guides(depth, last, endedAbove)) line.Children.Add(guide);
        line.Children.Add(content);
        return line;
    }

    /// <summary>Folds or unfolds a parent's children, as a click on its row does.</summary>
    internal void Toggle(string key)
    {
        if (!folding.TryGetValue(key, out var slot) || slot.Parts is not { } parts) return;
        var open = !parts.IsVisible;
        pane.SetCollapsed(key, slot.Fold.DefaultOpen ? !open : open);
        parts.IsVisible = open;
        ClaudeTree.SetChevron(slot.Chevron, open);
        if (slot.Stem is not null) slot.Stem.IsVisible = open;
    }

    // The name, then a chevron when there is something to fold; the whole head row is the click target.
    private Control Disclosure(Control name, DockPanel head, ClaudeFold group)
    {
        var open = IsOpen(group);
        var chevron = ClaudeTree.Chevron(open);
        chevron.IsVisible = group.HasChildren;
        var named = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, chevron } };
        if (!group.HasChildren) return named;
        head.Cursor = new Cursor(StandardCursorType.Hand);
        head.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(head).Properties.IsLeftButtonPressed) return;
            Toggle(group.Key);
            args.Handled = true;
        };
        return named;
    }

    // The parent's own glyph carries the stem; the wrapper keeps the row's name, tag and bottom rule.
    private Control Parent(StackPanel row, ClaudeFold group)
    {
        var glyph = ClaudeTree.Glyph(Ui.Icon("box", Ui.Hint, 14), group.HasChildren);
        var chevron = row.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().First(path => path.Name == "ClaudeCapabilityChevron");
        folding[group.Key] = new(chevron, glyph.Children.OfType<Border>().FirstOrDefault(border => border.Name == "ClaudeContextStem"), group);
        if (folding[group.Key].Stem is { } stem) stem.IsVisible = IsOpen(group);
        row.Margin = new Thickness(0);
        var line = new DockPanel { Margin = new Thickness(8, 4) };
        line.Children.Add(glyph);
        line.Children.Add(row);
        return new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = line };
    }

    private Control NoMarketplaceRow(ClaudePluginGroup group)
    {
        var row = new StackPanel { Name = "ClaudeCapabilityGroup", Tag = group.Key, Margin = new Thickness(8, 4) };
        var head = new DockPanel { Background = Brushes.Transparent };
        head.Children.Add(Disclosure(Ui.Text(group.Label, Ui.TextBrush, 13), head, group.Fold));
        row.Children.Add(head);
        var hint = Ui.Text("Installed from a marketplace that is not listed, or from a local folder", Ui.Hint, 11);
        row.Children.Add(hint);
        return Parent(row, group.Fold);
    }

    /// <summary>What a row's menu offers, in order.</summary>
    internal IReadOnlyList<ClaudeCapabilityAction> Actions(ClaudeCapability item)
    {
        var actions = new List<ClaudeCapabilityAction>();
        if (ToggleFor(item) is { } toggle) actions.Add(new("ClaudeCapabilityToggle", item.Enabled ? "Turn off" : "Turn on", () => pane.Review(toggle)));
        var writable = WritableScope(item);
        if (item.Kind == ClaudeCapabilityKind.Marketplace && writable is { } ownScope)
        {
            actions.Add(new("ClaudeMarketplaceUpdate", "Update from source…", () => pane.Marketplace(new UpdateMarketplace(item.Name))));
            actions.Add(new("ClaudeMarketplaceRemove", "Remove…", () => pane.Marketplace(new RemoveMarketplace(item.Name, ownScope))));
        }
        if (item.Kind == ClaudeCapabilityKind.Plugin && writable is { } from)
        {
            foreach (var to in ClaudeValues.WritableScopes.Where(scope => scope != from))
                actions.Add(new("ClaudeCapabilityMove_" + to.Name(), $"Move to {to.Name()}…", () => pane.Move(new(item.Name, from, to)), ClaudeValues.PluginScopeWording(to)));
            actions.Add(new("ClaudeCapabilityUninstall", "Uninstall…", () => pane.Uninstall(new(item.Name, from))));
        }
        // A skill is a folder in the user's or the project's skills directory; plugin and managed skills are not listed here.
        if (item.Kind == ClaudeCapabilityKind.Skill && item.Origin.Path is not null)
        {
            if (item.Origin.Scope == ClaudeConfigScope.User && pane.ProjectRoot is { Length: > 0 })
                actions.Add(new("ClaudeSkillMove_project", "Move to project…", () => pane.MoveSkill(new(item.Name, ClaudeWritableScope.User, ClaudeWritableScope.Project)),
                    "Keep it in the project's .claude/skills folder. Only this project uses it."));
            else if (item.Origin.Scope == ClaudeConfigScope.Project)
                actions.Add(new("ClaudeSkillMove_user", "Move to user…", () => pane.MoveSkill(new(item.Name, ClaudeWritableScope.Project, ClaudeWritableScope.User)),
                    "Keep it in ~/.claude/skills. All your projects use it."));
        }
        return actions;
    }

    private Control Row(ClaudeCapability item, ClaudeFold? group = null, bool underMarketplace = false)
    {
        var row = new StackPanel { Name = "ClaudeCapability", Tag = item, Margin = new Thickness(8, 4) };
        var head = new DockPanel { Background = Brushes.Transparent };
        var actions = Actions(item);
        if (actions.Count > 0)
        {
            Button? menuButton = null;
            menuButton = Ui.IconButton("more", $"Actions for {item.Name}", () =>
            {
                var menu = new ContextMenu { Name = "ClaudeCapabilityActions", Placement = PlacementMode.BottomEdgeAlignedRight, PlacementTarget = menuButton };
                foreach (var action in actions)
                {
                    var entry = Ui.Menu(action.Label, action.Run);
                    entry.Name = action.Name;
                    if (action.Tip is not null) ToolTip.SetTip(entry, action.Tip);
                    menu.Items.Add(entry);
                }
                menuButton!.ContextMenu = menu;
                menu.Open(menuButton);
            });
            menuButton.Name = "ClaudeCapabilityMenu";
            menuButton.Width = menuButton.Height = 22;
            menuButton.Padding = new Thickness(4);
            DockPanel.SetDock(menuButton, Dock.Right);
            head.Children.Add(menuButton);
        }
        if (!item.Enabled)
        {
            var off = Ui.Text(item.DisabledBy is { } by ? $"OFF · {by.Scope.Name().ToUpperInvariant()}" : "OFF", Ui.Hint, 10);
            off.Name = "ClaudeCapabilityOff";
            off.Margin = new Thickness(4, 0);
            if (item.DisabledBy?.Path is not null) ToolTip.SetTip(off, $"Switched off in {item.DisabledBy.Scope.Name()} settings");
            DockPanel.SetDock(off, Dock.Right);
            head.Children.Add(off);
        }
        if (item.Origin.Path is null)
        {
            var chip = ClaudeParts.ScopeChip(item.Origin.Scope.Name());
            chip.Margin = new Thickness(4, 0);
            DockPanel.SetDock(chip, Dock.Right);
            head.Children.Add(chip);
        }
        if (item.Status is { } status)
        {
            var badge = StatusBadge(status, item.StatusText);
            badge.Margin = new Thickness(4, 0);
            DockPanel.SetDock(badge, Dock.Right);
            head.Children.Add(badge);
        }
        Control name = Ui.Text(item.Name, Ui.TextBrush, 13);
        if (group is not null) name = Disclosure(name, head, group);
        head.Children.Add(name);
        row.Children.Add(head);
        if (item.DisabledBy?.Path is { } disabledPath && disabledPath != item.Origin.Path)
            row.Children.Add(ClaudeParts.SourceButton(disabledPath, item.DisabledBy.KeyPath, pane.Open));
        if (item.Origin.Path is { } path)
        {
            var source = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            source.Children.Add(ClaudeParts.SourceButton(path, item.Origin.KeyPath, pane.Open));
            source.Children.Add(ClaudeParts.ScopeChip(item.Origin.Scope.Name()));
            row.Children.Add(source);
        }
        // Under its marketplace a plugin's marketplace name is the parent row; elsewhere it stays on the row.
        if (item.Detail is { } detail && !underMarketplace)
        {
            var line = ClaudeParts.Code(detail, Ui.Hint);
            if (item.Status is not null) ToolTip.SetTip(line, detail);
            row.Children.Add(line);
        }
        if (item.Kind == ClaudeCapabilityKind.Plugin)
        {
            var summary = Ui.Text(PartsSummary(item), Ui.Hint, 11);
            summary.Name = item.Parts is null ? "ClaudePluginMissing" : "ClaudePluginSummary";
            row.Children.Add(summary);
        }
        return group is not null ? Parent(row, group) : new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }

    /// <summary>The health of a server as a short coloured word; an unknown status shows Claude's own text, muted.</summary>
    internal static TextBlock StatusBadge(ClaudeMcpStatus status, string? raw)
    {
        var (text, brush) = status switch
        {
            ClaudeMcpStatus.Connected => ("Connected", (IBrush)Ui.Success),
            ClaudeMcpStatus.NeedsAuth => ("Needs sign-in", Ui.Warning),
            ClaudeMcpStatus.Pending => ("Starting", Ui.Warning),
            ClaudeMcpStatus.Failed => ("Failed", Ui.Danger),
            ClaudeMcpStatus.Disabled => ("Disabled", Ui.Muted),
            _ => (raw ?? "Unknown", Ui.Muted)
        };
        var badge = Ui.Text(text, brush, 10);
        badge.Name = "ClaudeMcpStatus";
        badge.Tag = status;
        return badge;
    }
}