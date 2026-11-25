using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Chat;
using Tereditor.Plugins.Chat.UI;

namespace Tereditor.Plugins.Acp.UI;

/// <summary>The ACP plugin's UI half (Early support): the chat document and the button that starts a chat with one of the configured agents.</summary>
public sealed class AcpUI : PluginUIModule
{
    public const string Label = "New ACP chat";

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var client = new PluginChatClient(context);
        // The agent edits files through the client methods the host serves, so the diff of a proposal has no file to read here.
        var selections = ChatDocuments.Register(context, client, AcpChat.Agent, (_, _) => Task.FromResult(""), request => AcpSignInSection.AuthenticateAsync(context, request));
        AcpTabIcons tabIcons = null!;
        tabIcons = new AcpTabIcons { AskAgents = workspace => _ = LoadTabIconsAsync(context, tabIcons, workspace) };
        var activity = new ChatActivityTracker(context);
        context.TabDecoration(tab => tabIcons.Decorate(tab) is { } icon
            ? activity.Decorate(tab, AcpContract.DocumentScheme) is { } mark ? icon with { Adornment = mark.Adornment } : icon
            : activity.Decorate(tab, AcpContract.DocumentScheme));
        _ = LoadTabIconsAsync(context, tabIcons, null);
        context.SettingsSection(new("ACP", AcpManifest.Manifest.Icon, () => new AcpSettingsView(context)));
        var agents = new AcpAgents();
        agents.Refresh(context);
        context.AgentPane(new("acp", "ACP", AcpManifest.Manifest.Icon, workspace => new AcpAgentPane(context, agents, workspace))
        {
            Usable = () => !agents.Loaded || agents.Enabled.Count > 0,
            Owns = tab => tab is PluginDocumentTabRef { Scheme: AcpContract.DocumentScheme },
            SubAgentOf = tab => tab is PluginDocumentTabRef { Scheme: AcpContract.DocumentScheme } chat ? tabIcons.AgentOf(chat.WorkspaceId, chat.Id) : null,
            SubAgents = _ => [.. agents.Enabled.Select(agent => new AgentSubAgent(agent.Id, agent.Name, tabIcons.IconOf(agent.Id)))],
            SelectSubAgent = (pane, id) => (pane as AcpAgentPane)?.Select(id),
            Retarget = (pane, workspace) => pane is AcpAgentPane acp && acp.Retarget(workspace),
            ShowTab = (pane, tab) => (pane as AcpAgentPane)?.ShowTab(tab)
        });
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("acp-chat", (workspace, group) =>
        {
            var button = new Button
            {
                Name = "NewAcp",
                Content = AcpGlyph.Create(16),
                Width = 32,
                Height = 32,
                Padding = new Avalonia.Thickness(8),
                Background = Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                CornerRadius = new Avalonia.CornerRadius(0)
            };
            AutomationProperties.SetName(button, Label);
            ToolTip.SetTip(button, Label);
            var menu = new ContextMenu { Name = "AcpLaunchMenu", Placement = PlacementMode.BottomEdgeAlignedLeft };
            // A touch screen has no right click: a tap opens the menu, which lists every agent.
            if (OperatingSystem.IsAndroid()) button.Click += (_, _) => menu.Open(button);
            else
            {
                ToolTip.SetTip(button, Label + " (right-click to choose an agent)");
                button.Click += (_, _) => _ = LaunchAsync(context, button, workspace, tabIcons);
            }
            menu.Opening += (_, _) => _ = FillMenuAsync(context, menu, workspace, tabIcons);
            _ = FillMenuAsync(context, menu, workspace, tabIcons);
            button.ContextMenu = menu;
            return button;
        }));
        var buffers = context.Subscribe(AcpContract.Buffer, request => _ = AnswerBufferAsync(context, request));
        return () => { selections.Dispose(); buffers.Dispose(); activity.Dispose(); };
    }

    /// <summary>Answers the host's read of a file: the unsaved text of the editor that holds it, or nothing. Always replies.</summary>
    private static async Task AnswerBufferAsync(IPluginUIContext context, AcpBufferRequest request)
    {
        string? text = null;
        try
        {
            if (context.Editors.List(request.WorkspaceId).FirstOrDefault(editor => editor.Kind == EditorKind.File && editor.Path == request.Path) is { Dirty: true } editor)
                text = context.Editors.UnsavedText(editor.Id);
        }
        catch (Exception error) when (error is not OperationCanceledException) { }
        try { await context.RequestAsync(AcpContract.BufferReply, new AcpBufferReply(request.Id, text)); }
        catch (Exception error) when (error is PluginCallException or InvalidOperationException) { }
    }

    /// <summary>Fills the right-click menu with every configured agent, the default marked, and a way to the settings. It shows the last list at once and refreshes while it opens.</summary>
    private static async Task FillMenuAsync(IPluginUIContext context, ContextMenu menu, string workspace, AcpTabIcons tabIcons)
    {
        AcpAgentList list;
        try { list = await Task.Run(() => context.RequestAsync(AcpContract.Agents, new AcpNoParams()).AsTask()); }
        catch (Exception error) when (error is not OperationCanceledException) { return; }
        var icons = await CachedIconsAsync(context);
        if (tabIcons.SetIcons(icons)) Dispatcher.UIThread.Post(context.Invalidate);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var defaultId = context.Settings<AcpSettings>().DefaultAgent;
            menu.Items.Clear();
            foreach (var (agent, isDefault) in AcpLauncher.MenuEntries(defaultId, list.Agents))
            {
                var item = new MenuItem { Name = "AcpLaunch_" + agent.Id, Header = isDefault ? agent.Name + " (default)" : agent.Name, Icon = AcpGlyph.For(icons.GetValueOrDefault(agent.Id), 14) };
                item.Click += (_, _) => _ = StartAsync(context, workspace, agent, tabIcons);
                menu.Items.Add(item);
            }
            if (list.Agents.Count > 0) menu.Items.Add(new Separator());
            var settings = new MenuItem { Name = "AcpLaunch_settings", Header = "ACP settings" };
            settings.Click += (_, _) => context.OpenSettings(UiPages.PluginSection(AcpManifest.Id));
            menu.Items.Add(settings);
        });
    }

    /// <summary>A left click: the default agent starts at once. Without a usable default it is the picker, or the only agent when there is just one.</summary>
    private static async Task LaunchAsync(IPluginUIContext context, Button anchor, string workspace, AcpTabIcons tabIcons)
    {
        AcpAgentList list;
        try { list = await Task.Run(() => context.RequestAsync(AcpContract.Agents, new AcpNoParams()).AsTask()); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't list ACP agents", error.Message);
            return;
        }
        var choice = AcpLauncher.Resolve(context.Settings<AcpSettings>().DefaultAgent, list.Agents);
        if (choice is { Kind: "start", Agent: { } agent }) { await StartAsync(context, workspace, agent, tabIcons); return; }
        var icons = await CachedIconsAsync(context);
        if (tabIcons.SetIcons(icons)) Dispatcher.UIThread.Post(context.Invalidate);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (choice.Kind == "none")
            {
                context.Notify(PluginNotificationKind.Info, "No ACP agent yet", "Add an agent in Settings under ACP.");
                return;
            }
            var menu = new ContextMenu { Name = "AcpAgentMenu", Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = anchor };
            foreach (var agent in AcpLauncher.Enabled(list.Agents))
            {
                var item = new MenuItem { Header = agent.Name, Icon = AcpGlyph.For(icons.GetValueOrDefault(agent.Id), 14) };
                item.Click += (_, _) => _ = StartAsync(context, workspace, agent, tabIcons);
                menu.Items.Add(item);
            }
            menu.Open(anchor);
        });
    }

    /// <summary>Reads the icons on disk and, for a workspace, the agent of each of its chats, so restored chat tabs show their agent's logo.</summary>
    private static async Task LoadTabIconsAsync(IPluginUIContext context, AcpTabIcons tabIcons, string? workspace)
    {
        var changed = tabIcons.SetIcons(await CachedIconsAsync(context));
        if (workspace is not null)
            try
            {
                var chats = await Task.Run(() => context.RequestAsync(AcpContract.ChatAgents, new AcpChatAgentsParams(workspace)).AsTask());
                changed |= tabIcons.SetAgents(workspace, chats.Agents);
            }
            catch (Exception error) when (error is not OperationCanceledException) { }
        if (changed) Dispatcher.UIThread.Post(context.Invalidate);
    }

    /// <summary>Asks the host for the icon of an agent whose chat just started (it fetches one the disk lacks) and refreshes the tabs when it arrives. Never awaited by the chat.</summary>
    private static async Task FetchAgentIconAsync(IPluginUIContext context, AcpTabIcons tabIcons, string agentId)
    {
        try
        {
            var set = await Task.Run(() => context.RequestAsync(AcpContract.AgentIcon, new AcpAgentIconParams(agentId)).AsTask());
            if (tabIcons.AddIcons(set.Svgs)) Dispatcher.UIThread.Post(context.Invalidate);
        }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    /// <summary>The icons already on disk. The menu never waits for the network: an agent without one shows the generic glyph.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> CachedIconsAsync(IPluginUIContext context)
    {
        try { return (await Task.Run(() => context.RequestAsync(AcpContract.Icons, new AcpIconParams(false, true)).AsTask())).Svgs; }
        catch (Exception error) when (error is not OperationCanceledException) { return new Dictionary<string, string>(); }
    }

    private static async Task StartAsync(IPluginUIContext context, string workspace, AcpAgentEntry agent, AcpTabIcons tabIcons)
    {
        var id = ChatDocuments.NewId();
        try { await Task.Run(() => context.RequestAsync(AcpContract.Choose, new AcpChooseParams(workspace, id, agent.Id)).AsTask()); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't start the chat", error.Message);
            return;
        }
        tabIcons.SetAgent(workspace, id, agent.Id);
        if (agent.Origin == "registry") _ = FetchAgentIconAsync(context, tabIcons, agent.Id);
        await Dispatcher.UIThread.InvokeAsync(() => ChatDocuments.Open(context, AcpChat.Agent, workspace, id, agent.Name, new ChatOpenParams(workspace, id), null, []));
    }
}