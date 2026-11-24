using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Tereditor.Checks.E2E;
using Tereditor.Plugins.Api.UI;
using Tereditor.UI.Panels;

using static Tereditor.Checks.E2E.E2eWorkspace;

namespace Tereditor.Checks;

/// <summary>
/// The Agent pane: chips only for usable agents in a fixed order, four tabs, the chip following the focused document, a manual chip kept
/// until an agent tab is focused again, and the sub-agent picker following the focused ACP chat.
/// </summary>
internal static class AgentPaneChecks
{
    /// <summary>Opens the Agent pane in the window and picks an agent's chip, as a user does.</summary>
    internal static Control Show(E2eWorkspace app, string chip)
    {
        // An agent whose program is missing has no chip; a check that sets no command of its own runs a program every machine has.
        var plugin = chip == "claude" ? "claude-code" : chip;
        var stored = app.Workbench.State.Current.PluginSettings.GetValueOrDefault(plugin);
        if (stored.ValueKind is not System.Text.Json.JsonValueKind.Object || !stored.TryGetProperty("command", out _))
        {
            var json = stored.ValueKind is System.Text.Json.JsonValueKind.Object ? System.Text.Json.Nodes.JsonNode.Parse(stored.GetRawText())!.AsObject() : [];
            json["command"] = "sh";
            _ = app.Workbench.State.ChangeAsync(Tereditor.Host.Abstractions.HostStateChange.PluginSettings(plugin, json.ToJsonString()));
        }
        app.Window.RevealToolFor(app.Window.WorkspaceRoot, "agent");
        try { Until(() => Find<ToggleButton>(app, "AgentChip_" + chip) is not null); }
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

    /// <summary>The pane composed from fake agents: chips, order, usability, follow, manual choice and the sub-agent picker.</summary>
    internal static void Compose()
    {
        var registry = new Tereditor.UI.Plugins.PluginRegistry();
        var shownTabs = new List<string>();
        var codexUsable = true;
        var acpPicked = new List<string?>();
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
                SubAgents = picker ? _ => [.. acpAgents.Select(agent => new AgentSubAgent(agent, agent))] : null,
                SelectSubAgent = picker ? (_, agent) => acpPicked.Add(agent) : null
            });
        }
        // Registered out of order on purpose: the row keeps the fixed order.
        Add("acp", "acp", tab => tab is PluginDocumentTabRef { Scheme: "acp-chat" }, tab => tab is PluginDocumentTabRef chat ? chat.Id : null, picker: true);
        Add("codex", "codex", tab => tab is TerminalTabRef { TabKey: "codex-tab" }, usable: () => codexUsable);
        Add("claude-code", "claude", tab => tab is TerminalTabRef { TabKey: "claude-tab" } or PluginDocumentTabRef { Scheme: "claude-chat" });
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
        var picker = pane.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "AgentSubAgents");
        Require(Checked(pane) == "acp" && picker.IsVisible && ((AgentSubAgent)picker.SelectedItem!).Id == "junie" && acpPicked.Last() == "junie", "A focused ACP chat shows the ACP chip and its agent in the picker.");
        focus = ("w|5", new PluginDocumentTabRef("/w", "acp-chat", "copilot")); pane.Sync();
        Require(((AgentSubAgent)picker.SelectedItem!).Id == "copilot" && acpPicked.Last() == "copilot", "Another ACP chat moves the picker.");
        Click(pane, "AgentChip_codex");
        Require(!picker.IsVisible, "The picker shows only for an agent that has sub-agents.");
        registry.RemovePlugin("claude-code");
        pane.Sync();
        Require(!Chips(pane).Contains("claude"), "Disabling a plugin removes its chip.");
        window.Close();
        Console.WriteLine("PASS agent pane: fixed chip order, usable agents only, follow focus, manual choice kept until an agent tab, sub-agent picker follows the chat");
    }

    internal static void Run(string root)
    {
        State();
        Compose();
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