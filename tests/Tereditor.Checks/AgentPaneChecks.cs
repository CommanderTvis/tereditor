using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Checks.E2E;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.UI.Panels;

using static Tereditor.Checks.E2E.E2eWorkspace;

namespace Tereditor.Checks;

/// <summary>
/// The Agent pane: chips only for usable agents in a fixed order, four tabs, the chip following the focused document, a manual chip kept
/// until an agent tab is focused again, and the sub-agent chips following the focused ACP chat.
/// </summary>
internal static class AgentPaneChecks
{
    /// <summary>
    /// A program every machine the checks run on has: <c>sh</c> for the agents that only need their file found, <c>git</c> for omp, whose status
    /// runs <c>--version</c> and needs exit code 0 (dash, the Linux <c>sh</c>, rejects the flag).
    /// </summary>
    internal static string StandIn(string plugin) => plugin == "omp" ? "git" : "sh";

    /// <summary>Opens the Agent pane in the window and picks an agent's chip, as a user does.</summary>
    internal static Control Show(E2eWorkspace app, string chip)
    {
        // An agent whose program is missing has no chip; a check that sets no command of its own gets a program every machine has.
        var plugin = chip == "claude" ? "claude-code" : chip;
        var stored = app.Workbench.State.Current.PluginSettings.GetValueOrDefault(plugin);
        // The stored command can be the plugin's own default ("codex"), which only a machine with that tool installed finds: replace anything that is not a path.
        var command = stored.ValueKind is System.Text.Json.JsonValueKind.Object && stored.TryGetProperty("command", out var current) && current.ValueKind is System.Text.Json.JsonValueKind.String ? current.GetString() : null;
        if (string.IsNullOrWhiteSpace(command) || (command.IndexOfAny(['/', '\\']) < 0 && command != StandIn(plugin)))
        {
            var json = stored.ValueKind is System.Text.Json.JsonValueKind.Object ? System.Text.Json.Nodes.JsonNode.Parse(stored.GetRawText())!.AsObject() : [];
            json["command"] = StandIn(plugin);
            _ = app.Workbench.State.ChangeAsync(Tereditor.Host.Abstractions.HostStateChange.PluginSettings(plugin, json.ToJsonString()));
        }
        app.Window.RevealToolFor(app.Window.WorkspaceRoot, "agent");
        // The pane can sit in the layout before the plugins finish loading, and its chips are rebuilt as agents become usable: click once they match what is offered.
        try { Until(() => Find<AgentPane>(app, "AgentPane") is { } shown && Chips(shown).SequenceEqual(shown.Offered().Select(offer => offer.Row.Value.Id)) && Chips(shown).Contains(chip)); }
        catch (InvalidOperationException)
        {
            Dispatcher.UIThread.RunJobs();
            var all = app.Window.GetLogicalDescendants().OfType<Control>().ToArray();
            throw new InvalidOperationException($"No chip {chip}: pane={all.Any(c => c.Name == "AgentPane")}, chips=[{string.Join(",", all.Where(c => c.Name?.StartsWith("AgentChip_") == true).Select(c => c.Name))}], tabs={all.Count(c => c.Name?.StartsWith("AgentTab_") == true)}, none={all.Any(c => c.Name == "AgentNone")}, agentRail=[{string.Join(",", all.Where(c => c.Name?.Contains("agent") == true).Select(c => c.Name))}], available={app.Window.Layout.IsToolAvailable("agent")}");
        }
        if (Find<ToggleButton>(app, "AgentChip_" + chip) is { IsChecked: not true } button) app.Click(button);
        return Find<Control>(app, "AgentPane")!;
    }

    /// <summary>Clicks one of the four tabs: context, settings, capabilities or account.</summary>
    internal static void Tab(E2eWorkspace app, string name)
    {
        Until(() => Find<ToggleButton>(app, "AgentTab_" + name) is not null);
        app.Click(Find<ToggleButton>(app, "AgentTab_" + name)!);
    }

    private static T? Find<T>(E2eWorkspace app, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs();
        return app.Window.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);
    }

    private static void Click(Control pane, string name)
    {
        var button = pane.GetLogicalDescendants().OfType<ToggleButton>().Single(control => control.Name == name);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] Chips(Control pane) =>
        [.. pane.GetLogicalDescendants().OfType<ToggleButton>().Where(control => control.Name?.StartsWith("AgentChip_", StringComparison.Ordinal) == true).Select(control => control.Name!["AgentChip_".Length..])];

    private static string? Checked(Control pane) =>
        pane.GetLogicalDescendants().OfType<ToggleButton>().Where(control => control.Name?.StartsWith("AgentChip_", StringComparison.Ordinal) == true && control.IsChecked == true)
            .Select(control => control.Name!["AgentChip_".Length..]).SingleOrDefault();

    /// <summary>The pane composed from fake agents: chips, order, usability, follow, manual choice and the sub-agent chips.</summary>
    internal static void Compose()
    {
        var registry = new Tereditor.UI.Plugins.PluginRegistry();
        var shownTabs = new List<string>();
        var codexUsable = true;
        var acpPicked = new List<string?>();
        var subjects = new List<string>();
        string[] acpAgents = ["copilot", "junie"];
        void Add(string plugin, string id, Func<TabRef, bool> owns, Func<TabRef, string?>? sub = null, Func<bool>? usable = null, bool picker = false)
        {
            registry.SetActive(plugin, true);
            registry.AddAgentPane(plugin, new(id, id.ToUpperInvariant(), "robot", _ => new TextBlock { Name = "Body_" + id })
            {
                Usable = usable,
                Owns = owns,
                SubAgentOf = sub,
                ShowTab = (_, tab) => shownTabs.Add(id + ":" + tab),
                Focus = (_, tab) => subjects.Add(id + ":" + tab switch { TerminalTabRef terminal => terminal.TabKey, PluginDocumentTabRef chat => chat.Id, _ => "?" }),
                SubAgents = picker ? _ => [.. acpAgents.Select(agent => new AgentSubAgent(agent, agent))] : null,
                SelectSubAgent = picker ? (_, agent) => acpPicked.Add(agent) : null
            });
        }
        // Registered out of order on purpose: the row keeps the fixed order.
        Add("acp", "acp", tab => tab is PluginDocumentTabRef { Scheme: "acp-chat" }, tab => tab is PluginDocumentTabRef chat ? chat.Id : null, picker: true);
        Add("codex", "codex", tab => tab is TerminalTabRef { TabKey: "codex-tab" or "codex-tab-2" }, usable: () => codexUsable);
        Add("claude-code", "claude", tab => tab is TerminalTabRef { TabKey: "claude-tab" or "claude-tab-2" } or PluginDocumentTabRef { Scheme: "claude-chat" });
        (string Key, TabRef Tab)? focus = null;
        var pane = new AgentPane(registry, () => "/w", () => focus, (_, create) => new ContentControl { Content = create() });
        var window = new Window { Content = pane, Width = 400, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Require(Chips(pane).SequenceEqual(["claude", "codex", "acp"]), "Chips follow the fixed order: " + string.Join(",", Chips(pane)));
        Require(Checked(pane) == "claude", "Without a focused agent tab the first chip shows.");
        Require(pane.GetLogicalDescendants().OfType<ToggleButton>().Count(control => control.Name?.StartsWith("AgentTab_", StringComparison.Ordinal) == true) == 4, "The four tabs are always there.");
        Click(pane, "AgentTab_account");
        Require(shownTabs.Last() == "claude:Account", "Choosing a tab shows it in the agent's content.");
        ViewTabs(pane);
        codexUsable = false;
        registry.Invalidate("codex");
        pane.Sync();
        Require(Chips(pane).SequenceEqual(["claude", "acp"]), "An agent that is not usable has no chip.");
        codexUsable = true; pane.Sync();
        focus = ("w|1", new TerminalTabRef("/w", "codex-tab")); pane.Sync();
        Require(Checked(pane) == "codex", "Focusing a Codex terminal shows the Codex chip.");
        focus = ("w|2", new FileTabRef("/w", EditorKind.File, "a.cs")); pane.Sync();
        Require(Checked(pane) == "codex", "Focusing a file keeps the last chip.");
        Click(pane, "AgentChip_claude");
        Require(Checked(pane) == "claude", "A chip click shows that agent even while a Codex tab was last focused.");
        focus = ("w|3", new FileTabRef("/w", EditorKind.File, "b.cs")); pane.Sync();
        Require(Checked(pane) == "claude", "A manual choice survives focus moving to a non-agent tab.");
        focus = ("w|1", new TerminalTabRef("/w", "codex-tab")); pane.Sync();
        Require(Checked(pane) == "codex", "Focusing an agent tab takes the chip back from a manual choice.");
        Click(pane, "AgentChip_claude");
        pane.Sync();
        Require(Checked(pane) == "claude", "Syncing the same focused tab does not undo a manual choice.");
        focus = ("w|4", new PluginDocumentTabRef("/w", "acp-chat", "junie")); pane.Sync();
        Require(!pane.GetLogicalDescendants().OfType<ComboBox>().Any(), "The pane has no dropdown.");
        var picker = pane.GetLogicalDescendants().OfType<WrapPanel>().Single(control => control.Name == "AgentSubAgents");
        string[] SubChips() => [.. picker.Children.OfType<ToggleButton>().Select(chip => ((string)chip.Tag!))];
        string? SubChecked() => picker.Children.OfType<ToggleButton>().Where(chip => chip.IsChecked == true).Select(chip => (string)chip.Tag!).SingleOrDefault();
        Require(Checked(pane) == "acp" && picker.IsVisible && SubChips().SequenceEqual(acpAgents) && SubChecked() == "junie" && acpPicked.Last() == "junie", "A focused ACP chat shows the ACP chip and one sub-chip per agent, its own checked.");
        focus = ("w|5", new PluginDocumentTabRef("/w", "acp-chat", "copilot")); pane.Sync();
        Require(SubChecked() == "copilot" && acpPicked.Last() == "copilot", "Another ACP chat moves the sub-chip selection.");
        Click(pane, "AgentSubAgent_junie");
        Require(SubChecked() == "junie" && acpPicked.Last() == "junie", "A click on a sub-chip selects that agent.");
        focus = ("w|5b", new PluginDocumentTabRef("/w", "acp-chat", "copilot")); pane.Sync();
        Require(SubChecked() == "copilot", "Focusing an ACP chat takes the sub-chip back from a manual choice.");
        // Two sessions of one agent: the content is told which one is focused, once, and keeps it while focus is on anything else.
        focus = ("w|6", new TerminalTabRef("/w", "claude-tab")); pane.Sync();
        Require(subjects.Last() == "claude:claude-tab", "Focusing a Claude terminal tells the Claude content which session it is: " + string.Join(",", subjects));
        var told = subjects.Count;
        pane.Sync();
        Require(subjects.Count == told, "Reporting the same focused tab again does not tell the content twice.");
        focus = ("w|7", new TerminalTabRef("/w", "claude-tab-2")); pane.Sync();
        Require(Checked(pane) == "claude" && subjects.Last() == "claude:claude-tab-2" && subjects.Count == told + 1, "Focusing a second session of the same agent changes the content's subject.");
        focus = ("w|8", new FileTabRef("/w", EditorKind.File, "c.cs")); pane.Sync();
        Require(subjects.Count == told + 1, "Focusing a file leaves the content on its last session.");
        focus = ("w|9", new PluginDocumentTabRef("/w", "claude-chat", "chat-1")); pane.Sync();
        Require(subjects.Last() == "claude:chat-1", "A focused chat of the agent is the subject too.");
        focus = ("w|10", new TerminalTabRef("/w", "codex-tab-2")); pane.Sync();
        Require(subjects.Last() == "codex:codex-tab-2" && !subjects.Skip(told + 2).Any(entry => entry.StartsWith("claude:", StringComparison.Ordinal)), "A Codex tab goes to the Codex content only.");
        Click(pane, "AgentChip_codex");
        Require(!picker.IsVisible, "The sub-agent chips show only for an agent that has several sub-agents.");
        registry.RemovePlugin("claude-code");
        pane.Sync();
        Require(!Chips(pane).Contains("claude"), "Disabling a plugin removes its chip.");
        window.Close();
        Console.WriteLine("PASS agent pane: fixed chip order, usable agents only, follow focus, manual choice kept until an agent tab, sub-agent chips follow the chat");
    }

    // The agent chips are filled pills; the view tabs are plain text with an accent underline under the selected one: no fill, no box.
    private static void ViewTabs(Control pane)
    {
        Dispatcher.UIThread.RunJobs();
        static Avalonia.Media.IBrush? Fill(ToggleButton button) =>
            button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(part => part.Name == "PART_ContentPresenter").Background;
        static bool Clear(Avalonia.Media.IBrush? brush) => brush is null || brush is Avalonia.Media.ISolidColorBrush { Color.A: 0 };
        var tabs = pane.GetLogicalDescendants().OfType<ToggleButton>().Where(control => control.Name?.StartsWith("AgentTab_", StringComparison.Ordinal) == true).ToArray();
        var chips = pane.GetLogicalDescendants().OfType<ToggleButton>().Where(control => control.Name?.StartsWith("AgentChip_", StringComparison.Ordinal) == true).ToArray();
        var selected = tabs.Single(tab => tab.IsChecked == true);
        var chip = chips.Single(control => control.IsChecked == true);
        static Border Underline(ToggleButton tab) => tab.GetLogicalDescendants().OfType<Border>().Single(part => part.Name == "AgentTabUnderline");
        Require(tabs.All(tab => Clear(Fill(tab)) && Clear(tab.Background) && Clear(tab.BorderBrush) && tab.BorderThickness == default),
            "A view tab has no fill and no border, selected or not: " + string.Join(";", tabs.Select(tab => $"{tab.Name} fill={Fill(tab)} ({(Fill(tab) as Avalonia.Media.ISolidColorBrush)?.Color}) bg={tab.Background} border={tab.BorderBrush}/{tab.BorderThickness}")));
        Require(Underline(selected).IsVisible && tabs.Where(tab => tab != selected).All(tab => !Underline(tab).IsVisible), "Only the selected view tab has the underline.");
        Require(tabs.All(tab => Underline(tab).Height == 2 && tab.Height == selected.Height), "Every tab reserves the underline's row, so selecting moves nothing.");
        Require(!Clear(Fill(chip)), "The selected agent chip keeps its fill.");
        Require(tabs.All(tab => !Equals(Fill(tab), Fill(chip)) && !Equals(tab.Background, Fill(chip))), "No view tab shares the selected chip's fill.");
        Require(chips.Where(other => other != chip).All(other => Clear(Fill(other))), "Chips that are not selected have no fill.");
    }

    internal static void Run(string root)
    {
        State();
        Presets();
        Compose();
    }

    /// <summary>Every layout preset seats the Agent pane in the right rail, where the old Claude Code, Codex and Pi panes were opened.</summary>
    internal static void Presets()
    {
        foreach (var name in new[] { "balanced", "focus", "review" })
        {
            var state = Tereditor.UI.Docking.DockState.Preset(name);
            Require(state.Groups.Count(group => group.Region == "right" && group.Tools.Any(tool => tool.Id == "agent")) == 1, $"The {name} preset seats the Agent pane once, on the right.");
            Require(Tereditor.UI.Docking.DockState.RetiredToolIds.All(id => state.Groups.All(group => group.Tools.All(tool => tool.Id != id))), $"The {name} preset holds no retired agent pane.");
        }
        Console.WriteLine("PASS agent pane presets: seated in the right rail of every preset");
    }

    /// <summary>
    /// Two sessions of one real agent in a workspace: the pane's content describes the focused tab's session, not the first shown one.
    /// </summary>
    internal static void FollowsSessions(E2eWorkspace app)
    {
        var layout = app.Window.Layout;
        string[] ids = ["agent-session-one", "agent-session-two"];
        foreach (var id in ids)
        {
            layout.NewTerminal(app.Center, id);
            Until(() => app.Window.GetLogicalDescendants().OfType<Tereditor.UI.Terminal.TerminalView>().Any(view => view.Launch.TabKey == id));
            app.State!.SetTerminalAgent(new TerminalRef(app.Root, id), new("claude", "sh"));
        }
        var pane = Show(app, "claude");
        var content = pane.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeConfigPanel");
        var focus = app.Workbench.PluginLoader!.Registry.AgentPanes.Single(row => row.PluginId == "claude-code" && row.Value.Id == "claude").Value.Focus!;
        string Heading() => Find<StackPanel>(app, "ClaudeConfigScope") is { Tag: "session" } scope ? scope.GetLogicalDescendants().OfType<TextBlock>().First().Text ?? "" : "";
        // Side by side, both sessions are shown; the focused one must win over whichever the host lists first.
        layout.GroupTabs(app.Center, ids[0], ids[1], "horizontal");
        Until(() => Heading().Length > 0);
        focus(content, new TerminalTabRef(app.Root, ids[0]));
        var first = Heading();
        focus(content, new TerminalTabRef(app.Root, ids[1]));
        var second = Heading();
        Require(first.Length > 0 && second.Length > 0 && first != second, $"Each focused session of the agent is the one its Context tab describes: '{first}' and '{second}'.");
        focus(content, new PluginDocumentTabRef(app.Root, "claude-chat", "chat-1"));
        Require(Find<StackPanel>(app, "ClaudeConfigScope") is { Tag: "workspace" }, "A focused chat describes the workspace.");
        RefreshSharesTitle(content, "ClaudeConfigScope", "ClaudeConfigRefresh");
        Console.WriteLine("PASS agent pane follows the focused session of one agent");
    }

    /// <summary>
    /// At narrow and ordinary widths the panel's refresh button sits in the same row as the context title: inside the scope block's
    /// vertical range, to its right, and the scope block (the title wraps) has height, so no row holds the button alone.
    /// </summary>
    internal static void RefreshSharesTitle(Control panel, string scopeName, string refreshName)
    {
        var scope = panel.GetLogicalDescendants().OfType<StackPanel>().Single(control => control.Name == scopeName);
        var button = panel.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == refreshName);
        var title = scope.GetLogicalDescendants().OfType<TextBlock>().First();
        foreach (var width in new double[] { 260, 420 })
        {
            panel.Measure(new Avalonia.Size(width, 600));
            panel.Arrange(new Avalonia.Rect(0, 0, width, 600));
            var origin = new Avalonia.Point(0, 0);
            var buttonBox = new Avalonia.Rect(button.TranslatePoint(origin, panel)!.Value, button.Bounds.Size);
            var scopeBox = new Avalonia.Rect(scope.TranslatePoint(origin, panel)!.Value, scope.Bounds.Size);
            var titleBox = new Avalonia.Rect(title.TranslatePoint(origin, panel)!.Value, title.Bounds.Size);
            Require(scope.IsVisible && scopeBox.Height > 0, $"At {width}px the title block is shown.");
            Require(buttonBox.Top >= scopeBox.Top - 1 && buttonBox.Top < titleBox.Bottom, $"At {width}px {refreshName} sits in the title's row (button {buttonBox}, title {titleBox}).");
            Require(titleBox.Right <= buttonBox.Left + 0.5 && buttonBox.Right <= width, $"At {width}px {refreshName} does not overlap the title (button {buttonBox}, title {titleBox}).");
        }
        panel.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The state machine alone: follow, manual choice, keeping the chip on a non-agent tab.</summary>
    internal static void State()
    {
        var state = new AgentPaneState();
        string[] chips = ["claude-code:claude", "codex:codex", "acp:acp"];
        Require(state.Shown(chips) == "claude-code:claude", "Without a choice the first chip shows.");
        state.Observe("a", "codex:codex", null);
        Require(state.Shown(chips) == "codex:codex" && !state.Manual, "Focusing a Codex tab shows the Codex chip.");
        state.Observe("b", null, null);
        Require(state.Shown(chips) == "codex:codex", "Focusing a tab with no agent keeps the last chip.");
        state.Choose("claude-code:claude");
        state.Observe("b", null, null);
        state.Observe("c", null, null);
        Require(state.Shown(chips) == "claude-code:claude" && state.Manual, "A manual chip stays while focus moves between non-agent tabs.");
        state.Observe("d", "acp:acp", "copilot");
        Require(state.Shown(chips) == "acp:acp" && state.SubAgent == "copilot" && !state.Manual, "Focusing an agent tab takes the chip and sub-agent back.");
        state.Choose("codex:codex");
        state.Observe("d", "acp:acp", "copilot");
        Require(state.Shown(chips) == "codex:codex" && state.Manual, "Reporting the same tab again does not undo a manual choice.");
        state.Observe("e", "acp:acp", "junie");
        Require(state.SubAgent == "junie" && !state.Manual, "Another ACP chat moves the sub-agent.");
        Require(state.Shown(["codex:codex"]) == "codex:codex" && state.Shown([]) is null, "A chip that is no longer offered falls back to the first one, or to none.");
        Console.WriteLine("PASS agent pane state: follow, manual choice, last chip kept, sub-agent follow");
    }
}