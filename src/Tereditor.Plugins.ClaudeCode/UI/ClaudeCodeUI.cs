using Avalonia.Threading;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.ClaudeCode.UI.Chat;

namespace Tereditor.Plugins.ClaudeCode.UI;

/// <summary>The Claude Code configuration pane, launcher and terminal integration.</summary>
public sealed class ClaudeCodeUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var store = new ClaudeCodeStore();
        var glyph = new ClaudeGlyph(context);
        var actions = new IdeActions(context);
        var chat = new PluginChatClient(context);
        var chatSelections = ChatDocuments.Register(context, chat, ClaudeChat.Agent(context),
            async (workspaceId, path) => (await context.RequestAsync(ClaudeCodeContract.ReadFile, new ReadFileParams(workspaceId, path))).Content);
        var hydrated = new HashSet<string>();
        var disposed = false;
        // The host looks for the configured command on its PATH; until it answers, and when it cannot decide, the launcher stays available.
        AgentInstall? install = null;
        async void Probe()
        {
            try
            {
                var read = await context.RequestAsync(ClaudeCodeContract.Installed, new NoParams());
                if (!disposed) Dispatcher.UIThread.Post(() => { install = read; context.Invalidate(); });
            }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }
        Probe();
        var probeOnSettings = context.OnSettings<ClaudeCodeSettings>(_ => Probe());
        var status = context.Subscribe(ClaudeCodeContract.Status, push =>
        {
            store.Apply(push);
            NotifyAway(context, store, push);
        });

        async void Hydrate(string workspace)
        {
            if (!hydrated.Add(workspace)) return;
            try
            {
                var rows = await context.RequestAsync(ClaudeCodeContract.StatusSnapshot, new(workspace));
                if (!disposed) foreach (var row in rows) store.Apply(row);
            }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }
        context.OnWorkspaceRemoved(workspace =>
        {
            hydrated.Remove(workspace);
            actions.ForgetWorkspace(workspace);
            store.EvictWorkspace(workspace);
        });
        context.SettingsSection(new("Claude Code", "asset:claude.svg", () => new ClaudeCodeSettingsSection(context)));
        context.KeymapAction(new("OpenConfig", "Open Claude Code configuration", "Claude Code", () =>
        {
            if (context.Host().ActiveWorkspaceId is { Length: > 0 } workspace) context.RevealTool(workspace, "agent");
        }));
        context.Launcher(new("claude", "Claude Code", "asset:claude.svg", options =>
        {
            var settings = context.Settings<ClaudeCodeSettings>();
            var command = settings.Command.Trim() is { Length: > 0 } configured ? configured : "claude";
            var parts = new List<string>();
            if (options.Model is { } model) parts.Add("--model " + model);
            if (options.SystemPrompt is { } systemPrompt) parts.Add("--append-system-prompt " + ClaudeLaunch.ShellQuotePath(systemPrompt));
            if (options.ResumeSessionId is { } session) parts.Add("--resume " + session);
            else if (options.InitialPrompt is not null || options.Images.Count > 0)
                parts.Add(ClaudeLaunch.ShellQuotePath(string.Join("\n\n", new[] { options.InitialPrompt }
                    .Concat(options.Images.Select(path => "Read the attached image: " + path)).Where(text => !string.IsNullOrWhiteSpace(text)))));
            var args = string.Join(' ', parts);
            return ClaudeLaunch.SessionCommand(command, args, settings.DisableAgentView,
                settings.AppendSystemPrompt, context.Host().HostPlatform, model: options.Model is null ? settings.Model : null, effort: settings.Effort,
                contributions: context.AgentLaunchContributions("claude"), sessionId: ClaudeLaunch.NewSessionId(args),
                permissionMode: settings.PermissionMode, advisor: settings.Advisor, chrome: settings.Chrome ? true : null);
        }, () => LauncherAvailability.Of(context.AgentLaunchContributions("claude"), install?.Reason("Claude Code"), install?.Installed))
        { CreateIcon = glyph.Create, Open = (workspace, options) => ChatLauncher.OpenFromLauncher(context, workspace, options), Models = () => [.. ClaudeLaunch.Models.Select(model => new LauncherModel(model.Id, model.Label, "asset:claude.svg"))] });
        context.AgentPane(new("claude", "Claude Code", "asset:claude.svg", workspace =>
        {
            Hydrate(workspace);
            return new ClaudeConfigPanel(context, glyph, workspace);
        })
        {
            Usable = () => LauncherAvailability.Of(context.AgentLaunchContributions("claude"), install?.Reason("Claude Code"), install?.Installed).Available,
            Owns = tab => tab switch
            {
                TerminalTabRef terminal => context.Host().Terminals.GetValueOrDefault(terminal.WorkspaceId)?
                    .FirstOrDefault(item => item.TabKey == terminal.TabKey)?.Agent?.Kind == "claude",
                PluginDocumentTabRef document => document.Scheme == ClaudeChat.DocumentScheme,
                _ => false
            },
            Retarget = (pane, workspace) =>
            {
                if (pane is not ClaudeConfigPanel panel) return false;
                Hydrate(workspace);
                return panel.Retarget(workspace);
            },
            ShowTab = (pane, tab) => (pane as ClaudeConfigPanel)?.ShowTab(tab),
            Focus = (pane, tab) => (pane as ClaudeConfigPanel)?.Focus(tab)
        });
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("claude-launch",
            (workspace, group) => ClaudeLauncherAction.Create(context, glyph, workspace, group, chat)));
        context.WatchHost(host => string.Join("\n", host.Terminals.SelectMany(pair =>
            pair.Value.Where(tab => tab.Agent?.Kind == "claude").Select(tab => pair.Key + "\t" + tab.TabKey))), (_, _) => context.Invalidate());
        context.FileAction(new("mention", target => ClaudeMention.Label(context, target), target => _ = ClaudeMention.SendAsync(context, target))
        { Icon = "asset:claude.svg" });
        var chatActivity = new ClaudeChatActivity(store);
        context.TabDecoration(tab =>
        {
            // A chat reports on the same status channel as a terminal, under its chat id; it wears the shared chat mark.
            if (tab is PluginDocumentTabRef { Scheme: ClaudeChat.DocumentScheme } document)
            {
                Hydrate(document.WorkspaceId);
                return new TabDecoration { Adornment = () => new ChatActivityMark(chatActivity, document.WorkspaceId, document.Id) };
            }
            if (tab is not TerminalTabRef terminal ||
                context.Host().Terminals.GetValueOrDefault(tab.WorkspaceId)?.FirstOrDefault(candidate => candidate.TabKey == terminal.TabKey)?.Agent?.Kind != "claude")
                return null;
            Hydrate(tab.WorkspaceId);
            return new()
            {
                Icon = "asset:claude.svg",
                Adornment = () => new ClaudeStatusAdornment(store, terminal.WorkspaceId, terminal.TabKey),
                IsWorking = () => store.Session(terminal.WorkspaceId, terminal.TabKey)?.Status == ClaudeCodeStatus.Running
            };
        });
        // The id `claude --resume` takes: the host's agent record carries it as soon as the process is seen.
        string? SessionOf(TerminalTabRef terminal) => context.Host().Terminals.GetValueOrDefault(terminal.WorkspaceId)?
            .FirstOrDefault(candidate => candidate.TabKey == terminal.TabKey)?.Agent is { Kind: "claude", SessionId: { Length: > 0 } id } ? id : null;
        context.TerminalTabAction(new("copy-session-id", terminal => SessionOf(terminal) is null ? null : "Copy session ID",
            terminal => { if (SessionOf(terminal) is { } id) context.CopyText(id, "Copied session ID"); }));
        context.TerminalAccessory(new(terminal =>
        {
            Hydrate(terminal.WorkspaceId);
            return new ClaudeTerminalAccessory(context, store, terminal);
        }));
        var editors = context.Editors.OnEvent(editorEvent => _ = Forward(editorEvent));
        async Task Forward(EditorEvent editorEvent)
        {
            try
            {
                if (editorEvent is EditorSelectionEvent selected)
                {
                    var selection = selected.Selection is { Mapped: false } ? null : selected.Selection;
                    await context.RequestAsync(ClaudeCodeContract.SelectionChanged, new(selected.Editor.WorkspaceId,
                        selected.Editor.Path, selection?.Text ?? "", new(selection?.StartLine ?? 1, selection?.StartColumn ?? 1,
                            selection?.EndLine ?? 1, selection?.EndColumn ?? 1)));
                }
                else if (editorEvent is EditorLifecycleEvent { Lifecycle: EditorLifecycle.Activated } activated)
                    await context.RequestAsync(ClaudeCodeContract.SelectionChanged, new(activated.Editor.WorkspaceId,
                        activated.Editor.Path, "", new(1, 1, 1, 1)));
                else if (editorEvent is EditorLifecycleEvent { Lifecycle: EditorLifecycle.Closed } closed)
                    await context.RequestAsync(ClaudeCodeContract.DocumentClosed, new(closed.Editor.WorkspaceId, closed.Editor.Path));
            }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }
        var ide = context.Subscribe(ClaudeCodeContract.IdeAction, request => _ = actions.HandleAsync(request));
        return () => { disposed = true; probeOnSettings.Dispose(); status.Dispose(); editors.Dispose(); ide.Dispose(); actions.Dispose(); chatSelections.Dispose(); };
    }

    // Only live pushes ask: a hydrated snapshot row is where a session already stood, not something that just happened.
    private static void NotifyAway(IPluginUIContext context, ClaudeCodeStore store, ClaudeCodeStatusPush push)
    {
        AgentAttentionReason? reason = push.Status switch
        {
            ClaudeCodeStatus.Blocked => AgentAttentionReason.Blocked,
            ClaudeCodeStatus.Done => AgentAttentionReason.Done,
            ClaudeCodeStatus.Failed => AgentAttentionReason.Failed,
            _ => null
        };
        // A status-only event: the turn ended, but something already notified for it.
        if (reason is null || push.Report.Notify == false) return;
        var report = push.Report;
        context.NotifyAttention(new(push.WorkspaceId, push.TabKey, AgentAttention.Title("Claude", reason.Value, report.Project),
            AgentAttention.Body(report.Summary, report.Response, report.Query, report.ErrorType))
        {
            StillNeeded = () => store.Session(push.WorkspaceId, push.TabKey)?.Status == push.Status,
            Reason = reason switch { AgentAttentionReason.Blocked => "blocked", AgentAttentionReason.Failed => "failed", _ => "done" }
        });
    }
}