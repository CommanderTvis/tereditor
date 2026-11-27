using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Chat;
using Tereditor.Plugins.Chat.UI;

namespace Tereditor.Plugins.Pi.UI;

/// <summary>The Pi plugin's UI half: Settings › Pi, the side pane, the chat document and the launcher that opens a chat.</summary>
public sealed class PiUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        PiStatus? status = null;
        var client = new PluginChatClient(context);
        // Pi edits files itself and proposes nothing to diff, so there is no file to read for a proposal.
        var selections = ChatDocuments.Register(context, client, PiChat.Agent, (_, _) => Task.FromResult(""));
        var activity = new ChatActivityTracker(context);
        context.TabDecoration(tab => activity.Decorate(tab, PiContract.DocumentScheme));
        _ = Task.Run(async () =>
        {
            try { status = await context.RequestAsync(PiContract.Status, new PiNoParams()); }
            catch (Exception error) when (error is not OperationCanceledException) { }
        });
        context.AgentPane(AgentConfigPane.Registration(context, PiManifest.Id, "Pi", PiManifest.Icon, workspace => new PiConfigPanel(context, workspace),
            () => status is not { Installed: false, Source: PiSources.System }, PiContract.DocumentScheme));
        context.SettingsSection(new("Pi", PiManifest.Icon, () => new PiSettingsView(context, () => status, current => status = current)));
        context.Launcher(new AgentLauncher(PiManifest.Id, "Pi", PiManifest.Icon, _ => context.Settings<PiSettings>().Command,
            () => status is { Installed: false, Source: PiSources.System } ? new(false, "Pi is not installed; see https://github.com/badlogic/pi-mono", false) : new(true, null, status?.Installed))
        {
            Open = (workspace, options) =>
            {
                var open = new ChatOpenParams(workspace, ChatDocuments.NewId()) { Model = options.Model, ResumeSessionId = options.ResumeSessionId };
                ChatDocuments.Open(context, PiChat.Agent, workspace, open.ChatId, "Pi", open, options.InitialPrompt, options.Images);
                return ValueTask.FromResult(true);
            }
        });
        // Like Claude's start button, a chat starts from the strip of a workspace's tabs.
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("pi-chat", (workspace, _) =>
        {
            var button = AgentConfigPane.StartButton("NewPi", "New Pi chat", "New Pi chat", PiGlyph.Create(16));
            button.Click += (_, _) =>
            {
                var id = ChatDocuments.NewId();
                ChatDocuments.Open(context, PiChat.Agent, workspace, id, "Pi", new ChatOpenParams(workspace, id), null, []);
            };
            return button;
        }));
        return () => { selections.Dispose(); activity.Dispose(); };
    }
}