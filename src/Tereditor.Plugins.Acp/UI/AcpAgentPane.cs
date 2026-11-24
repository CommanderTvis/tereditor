using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Acp.UI;

/// <summary>What the Agent pane knows of the configured ACP agents: a copy read from the host, so the pane and its picker answer from memory.</summary>
internal sealed class AcpAgents
{
    private AcpAgentList? list;
    private string key = "";

    /// <summary>False until the host has answered once.</summary>
    public bool Loaded => list is not null;

    public IReadOnlyList<AcpAgentEntry> Enabled => list is null ? [] : AcpLauncher.Enabled(list.Agents);

    public AcpAgentEntry? Find(string? id) => list?.Agents.FirstOrDefault(agent => agent.Id == id);

    /// <summary>Reads the agents again off the UI thread; invalidates the plugin's contributions when the list changed.</summary>
    public void Refresh(IPluginUIContext context) => _ = Task.Run(async () =>
    {
        try
        {
            var read = await context.RequestAsync(AcpContract.Agents, new AcpNoParams());
            var next = string.Join('\n', read.Agents.Select(agent => $"{agent.Id}|{agent.Name}|{agent.Enabled}|{agent.DefaultMode}|{agent.Command}|{agent.Version}"));
            if (next == key) return;
            Dispatcher.UIThread.Post(() => { list = read; key = next; context.Invalidate(); });
        }
        catch (Exception error) when (error is not OperationCanceledException) { }
    });
}

/// <summary>
/// The ACP agent's side of the Agent pane: the agent the picker or the focused chat names, under the four tabs. It reads what the agent's entry
/// says and runs the sign-in the settings page runs; the registry and the entry's editing stay in Settings, ACP.
/// </summary>
internal sealed class AcpAgentPane : DockPanel
{
    private readonly IPluginUIContext context;
    private readonly AcpAgents agents;
    private readonly ContentControl body = new() { HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private string workspaceId;
    private string? agentId;
    private AgentPaneTab tab = AgentPaneTab.Context;
    private string rendered = "";

    public AcpAgentPane(IPluginUIContext context, AcpAgents agents, string workspaceId)
    {
        this.context = context; this.agents = agents; this.workspaceId = workspaceId;
        Name = "AcpAgentPane";
        Background = Ui.Sidebar;
        Children.Add(new ScrollViewer { Name = "AcpAgentBody", Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        AttachedToVisualTree += (_, _) => { agents.Refresh(context); Render(); };
    }

    internal bool Retarget(string next)
    {
        if (next == workspaceId) return true;
        workspaceId = next;
        rendered = "";
        Render();
        return true;
    }

    internal void Select(string? id) { if (agentId == id) return; agentId = id; Render(); }

    internal void ShowTab(AgentPaneTab next) { if (tab == next) return; tab = next; Render(); }

    private static TextBlock Line(string text, bool hint = false, string? name = null) =>
        new() { Text = text, Name = name, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = hint ? Ui.Hint : Ui.Muted };

    private void Render()
    {
        var entry = agents.Find(agentId) ?? agents.Enabled.FirstOrDefault();
        var key = tab + "|" + entry?.Id + "|" + entry?.Name + "|" + entry?.Version + "|" + entry?.DefaultMode + "|" + entry?.Command + "|" + workspaceId + "|" + agents.Loaded;
        if (key == rendered) return;
        rendered = key;
        var panel = new StackPanel { Margin = new Thickness(8, 4), Spacing = 6 };
        if (entry is null)
        {
            panel.Children.Add(Line(agents.Loaded ? "No ACP agent is set up. Add one in Settings, ACP." : "Reading the ACP agents...", name: "AcpAgentNone"));
            panel.Children.Add(SettingsLink());
            body.Content = panel;
            return;
        }
        switch (tab)
        {
            case AgentPaneTab.Context:
                panel.Children.Add(Ui.Text(entry.Name, Ui.TextBrush, 13));
                if (entry.Description is { Length: > 0 } description) panel.Children.Add(Line(description));
                if (entry.Version is { Length: > 0 } version) panel.Children.Add(Line("Version " + version, hint: true));
                panel.Children.Add(Line("Runs: " + string.Join(' ', new[] { entry.CommandOverride.Length > 0 ? entry.CommandOverride : entry.Command }.Concat(entry.Args)), hint: true, name: "AcpAgentRuns"));
                panel.Children.Add(Line("Folder: " + (entry.Cwd.Length > 0 ? entry.Cwd : ScopedSetting.AbbreviateHomePath(workspaceId)), hint: true));
                panel.Children.Add(Line("The agent reports its own context in the chat."));
                break;
            case AgentPaneTab.Settings:
                var mode = entry.DefaultMode.Length > 0 ? entry.KnownModes.FirstOrDefault(known => known.Id == entry.DefaultMode)?.Name ?? entry.DefaultMode : "the agent's default";
                panel.Children.Add(Line("A new chat with " + entry.Name + " starts in " + mode + ".", name: "AcpAgentMode"));
                panel.Children.Add(Line("Change the mode, the launch command and the folder in Settings, ACP.", hint: true));
                panel.Children.Add(SettingsLink());
                break;
            case AgentPaneTab.Capabilities:
                panel.Children.Add(Line(entry.Name + " has nothing here yet.", name: "AcpAgentNothing"));
                break;
            default:
                panel.Children.Add(new AcpSignInSection(context, entry.Id));
                panel.Children.Add(SettingsLink());
                break;
        }
        body.Content = panel;
    }

    private Control SettingsLink()
    {
        var link = Ui.Button("Open ACP settings", () => context.OpenSettings(UiPages.PluginSection(AcpManifest.Id)));
        link.Name = "AcpAgentSettings";
        link.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        return link;
    }
}
