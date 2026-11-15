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
    void Open(string path, IReadOnlyList<string>? keyPath);
    void Review(PendingEdit pending);
    void Uninstall(PluginUninstallTarget target);
    void Move(PluginMoveTarget target);
    void Marketplace(ClaudeMarketplaceAction action);
    void Add(string kind);
    bool IsCollapsed(string key);
    void SetCollapsed(string key, bool collapsed);
}

/// <summary>A marketplace with the plugins installed from it; a null marketplace is the group of plugins whose marketplace is not listed.</summary>
internal sealed record ClaudePluginGroup(ClaudeCapability? Marketplace, IReadOnlyList<ClaudeCapability> Plugins)
{
    public string Key => "plugins/" + (Marketplace?.Name ?? "\0none");
    public string Label => Marketplace?.Name ?? "No marketplace";
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

    private sealed class Folding(Avalonia.Controls.Shapes.Path chevron, Control? stem)
    {
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
            section.Children.Add(group.Marketplace is { } market ? Row(market, group) : NoMarketplaceRow(group));
            if (group.Plugins.Count == 0) continue;
            var parts = new StackPanel { Name = "ClaudeCapabilityChildren", Tag = group.Key, IsVisible = !pane.IsCollapsed(group.Key) };
            for (var index = 0; index < group.Plugins.Count; index++)
                parts.Children.Add(Child(Row(group.Plugins[index], underMarketplace: group.Marketplace is not null), last: index == group.Plugins.Count - 1));
            folding[group.Key].Parts = parts;
            section.Children.Add(parts);
        }
    }

    private static Control Child(Control content, bool last)
    {
        var line = new DockPanel();
        foreach (var guide in ClaudeTree.Guides(1, last)) line.Children.Add(guide);
        line.Children.Add(content);
        return line;
    }

    /// <summary>Folds or unfolds a parent's children, as a click on its row does.</summary>
    internal void Toggle(string key)
    {
        if (!folding.TryGetValue(key, out var slot) || slot.Parts is not { } parts) return;
        var open = !parts.IsVisible;
        pane.SetCollapsed(key, !open);
        parts.IsVisible = open;
        ClaudeTree.SetChevron(slot.Chevron, open);
        if (slot.Stem is not null) slot.Stem.IsVisible = open;
    }

    // The name, then a chevron when there is something to fold; the whole head row is the click target.
    private Control Disclosure(Control name, DockPanel head, ClaudePluginGroup group)
    {
        var open = !pane.IsCollapsed(group.Key);
        var chevron = ClaudeTree.Chevron(open);
        chevron.IsVisible = group.Plugins.Count > 0;
        var named = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, chevron } };
        if (group.Plugins.Count == 0) return named;
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
    private Control Parent(StackPanel row, ClaudePluginGroup group)
    {
        var glyph = ClaudeTree.Glyph(Ui.Icon("box", Ui.Hint, 14), group.Plugins.Count > 0);
        var chevron = row.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().First(path => path.Name == "ClaudeCapabilityChevron");
        folding[group.Key] = new(chevron, glyph.Children.OfType<Border>().FirstOrDefault(border => border.Name == "ClaudeContextStem"));
        if (folding[group.Key].Stem is { } stem) stem.IsVisible = !pane.IsCollapsed(group.Key);
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
        head.Children.Add(Disclosure(Ui.Text(group.Label, Ui.TextBrush, 13), head, group));
        row.Children.Add(head);
        var hint = Ui.Text("Installed from a marketplace that is not listed, or from a local folder", Ui.Hint, 11);
        row.Children.Add(hint);
        return Parent(row, group);
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
        return actions;
    }

    private Control Row(ClaudeCapability item, ClaudePluginGroup? group = null, bool underMarketplace = false)
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
        if (item.Detail is { } detail && !underMarketplace) row.Children.Add(ClaudeParts.Code(detail, Ui.Hint));
        return group is not null ? Parent(row, group) : new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }
}
