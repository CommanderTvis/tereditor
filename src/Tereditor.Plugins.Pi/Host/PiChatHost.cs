using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>What the host remembers of a chat so reopening its tab continues the same Pi session.</summary>
internal sealed record PiChatRecord(string WorkspaceId, string ChatId)
{
    public string? SessionFile { get; init; }
}

/// <summary>The registry of Pi chats and the handler of every <see cref="ChatContract"/> method.</summary>
internal sealed class PiChatHost(IPluginHostContext context, Func<string>? sessionsRoot = null, PiManaged? managed = null)
{
    private const string RecordsState = "chats";

    private readonly ConcurrentDictionary<(string, string), PiChatSession> sessions = new();
    private readonly ChatActivityBoard activity = new();
    // The commands of each open chat as last announced to the Pi pane; a chat that is not open has no entry.
    private readonly ConcurrentDictionary<(string, string), string> listed = new();
    private readonly Lock recordsGate = new();
    private readonly Func<string> sessionsRoot = sessionsRoot ?? (() => ContributedEnvironment(context).GetValueOrDefault("PI_CODING_AGENT_DIR") is { } agent ? Path.Combine(agent, "sessions") : DefaultSessionsRoot());

    /// <summary>
    /// The variables other plugins' launch contributions set for Pi, such as the agent directory of a chosen model source. Only plain
    /// variable names are taken, and never one under the <c>TEREDITOR_</c> prefix, the same rule a launch line follows.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ContributedEnvironment(IPluginHostContext context)
    {
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in context.AgentLaunchContributions(PiManifest.Id).SelectMany(contribution => contribution.Environment))
            if (name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                && !name.StartsWith("TEREDITOR_", StringComparison.Ordinal) && !name.StartsWith("THINKRAIL_", StringComparison.Ordinal))
                taken.TryAdd(name, value);
        return taken;
    }

    /// <summary>Pi's session store: <c>~/.pi/agent/sessions</c>, or <c>PI_CODING_AGENT_DIR/sessions</c> when that is set.</summary>
    public static string DefaultSessionsRoot() =>
        Path.Combine(Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } agent ? agent
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent"), "sessions");

    /// <summary>The header of the workspace's running Pi chat, the one that lists the most commands, for the Pi pane; null when none runs.</summary>
    internal ChatHeader? HeaderOf(string workspaceId) =>
        sessions.Where(pair => pair.Key.Item1 == workspaceId && pair.Value.Active).Select(pair => pair.Value.Header)
            .Where(header => !Over(header)).OrderByDescending(header => header.Commands.Count).FirstOrDefault();

    // A Pi that just ended can still look alive for a moment, so its status decides too.
    private static bool Over(ChatHeader header) => header.Status is ChatStatus.Exited or ChatStatus.Failed;

    // Headers are pushed often (status, usage), so the pane hears only of a chat that opens, lists other commands or ends.
    private void Announce(ChatHeaderPush push)
    {
        var chat = (push.WorkspaceId, push.ChatId);
        if (!Over(push.Header) && sessions.TryGetValue(chat, out var session) && session.Active)
        {
            var commands = string.Join('\n', push.Header.Commands.Select(command => $"{command.Name}\t{command.Source}\t{command.Description}"));
            if (listed.TryGetValue(chat, out var last) && last == commands) return;
            listed[chat] = commands;
        }
        else if (!listed.TryRemove(chat, out _)) return;
        context.Publish(PiContract.ConfigChange, new PiConfigChanged(push.WorkspaceId));
    }

    private static string RecordKey(string workspaceId, string chatId) => workspaceId + "\0" + chatId;

    private PiChatRecord? LoadRecord(string workspaceId, string chatId) =>
        context.ReadState<Dictionary<string, PiChatRecord>>(RecordsState, []).GetValueOrDefault(RecordKey(workspaceId, chatId));

    private void SaveSessionFile(string workspaceId, string chatId, string? file)
    {
        lock (recordsGate)
        {
            var all = context.ReadState<Dictionary<string, PiChatRecord>>(RecordsState, []);
            all[RecordKey(workspaceId, chatId)] = new PiChatRecord(workspaceId, chatId) { SessionFile = file };
            context.WriteState(RecordsState, all);
        }
    }

    private PiChatSession Session(string workspaceId, string chatId) =>
        sessions.TryGetValue((workspaceId, chatId), out var session) ? session : throw new InvalidOperationException("Unknown chat: " + chatId);

    private void Publish(ChatPush push)
    {
        try
        {
            context.Publish(ChatContract.Updates, push);
            if (push is ChatHeaderPush header) Announce(header);
            if (activity.Observe(push) is { } changed) context.Publish(ChatContract.Activity, changed);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            context.Log.Warn($"could not publish a chat {push.GetType().Name}: {error.Message}");
        }
    }

    public void Register()
    {
        context.Method(ChatContract.Open, (p, _, _) => OpenAsync(p));
        context.Method(ChatContract.ActivitySnapshot, (p, _, _) => ValueTask.FromResult(activity.Snapshot(p.WorkspaceId)));
        context.Method(ChatContract.Snapshot, (p, _, _) => ValueTask.FromResult(sessions.TryGetValue((p.WorkspaceId, p.ChatId), out var session) ? session.Snapshot() : []));
        context.Method(ChatContract.Send, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).SendAsync(p.Text, p.Attachments, p.ClientId);
            return new Ack();
        });
        context.Method(ChatContract.Interrupt, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).InterruptAsync();
            return new Ack();
        });
        context.Method(ChatContract.Close, async (p, _, _) =>
        {
            if (sessions.TryRemove((p.WorkspaceId, p.ChatId), out var session)) await session.DisposeAsync();
            if (activity.Set(p.WorkspaceId, p.ChatId, ChatActivity.Idle) is { } closed) context.Publish(ChatContract.Activity, closed);
            return new Ack();
        });
        context.Method(ChatContract.SetModel, async (p, _, _) => await Session(p.WorkspaceId, p.ChatId).SetModelAsync(p.Value));
        context.Method(ChatContract.SetEffort, async (p, _, _) => await Session(p.WorkspaceId, p.ChatId).SetEffortAsync(p.Value));
        context.Method(ChatContract.SetPermissionMode, (p, _, _) => ValueTask.FromResult(Session(p.WorkspaceId, p.ChatId).Header));
        context.Method(ChatContract.Answer, async (p, _, _) => { await Session(p.WorkspaceId, p.ChatId).AnswerAsync(p.RequestId, p.OptionId); return new Ack(); });
        context.Method(ChatContract.AnswerQuestion, async (p, _, _) => { await Session(p.WorkspaceId, p.ChatId).AnswerQuestionAsync(p.RequestId, p.Answers, p.Cancelled == true); return new Ack(); });
        context.Method(ChatContract.SetDraft, (p, _, _) => ValueTask.FromResult(Session(p.WorkspaceId, p.ChatId).SetDraft(p.Text, p.ClientId)));
        context.Method(ChatContract.Dequeue, (_, _, _) => throw Unsupported("Pi does not take back a queued message."));
        context.Method(ChatContract.EditQueued, (_, _, _) => throw Unsupported("Pi does not change a queued message."));
        context.Method(ChatContract.Rewind, (_, _, _) => throw Unsupported("Pi cannot rewind here yet."));
        context.Method(ChatContract.RewindPreview, (_, _, _) => throw Unsupported("Pi cannot rewind here yet."));
        context.Method(ChatContract.Fork, (_, _, _) => throw Unsupported("Pi cannot fork here yet."));
        context.Method(ChatContract.StopTask, (_, _, _) => throw Unsupported("Pi has no background tasks."));
        context.Method(ChatContract.ItemDetail, (p, _, _) => ValueTask.FromResult(new ChatDetail(Session(p.WorkspaceId, p.ChatId).ItemDetail(p.ItemId) ?? "")));
        context.Method(ChatContract.Sessions, (p, _, ct) => new ValueTask<ChatSessionList>(Task.Run(async () => ListSessions(await RootOf(p.WorkspaceId, ct)), ct)));
        context.Method(ChatContract.Files, async (p, _, ct) => await FindFilesAsync(p, ct));
    }

    private static NotSupportedException Unsupported(string message) => new(message);

    public async Task DisposeAsync()
    {
        var all = sessions.Values.ToArray();
        sessions.Clear();
        await Task.WhenAll(all.Select(session => session.DisposeAsync().AsTask()));
    }

    /// <summary>
    /// The pinned Pi runs on Bun, so a Model source that replaces the executable with its own launcher (a wrapper such as `tool run pi --`) cannot start it:
    /// the start is blocked with a sentence naming the source. JetBrains (Air Gateway) does not do this for Pi; it adds Central's extension instead.
    /// </summary>
    internal static string? ManagedBlock(IReadOnlyList<AgentLaunchContribution> contributions) =>
        contributions.FirstOrDefault(contribution => contribution.ReplacesExecutable && contribution.Wrapper.Count > 0) is { } replacer
            ? $"{(replacer.Provider is { Length: > 0 } name ? name : "The chosen Model source")} starts Pi itself, so the pinned Pi cannot run with it. Switch Pi source to System command, or choose another Model source."
            : null;

    /// <summary>The ids of the models the open chats' Pi listed, for the Settings model suggestions.</summary>
    internal IReadOnlyList<string> ReportedModels() =>
        [.. sessions.Values.Where(session => session.Active).SelectMany(session => session.Header.Models ?? []).Select(model => model.Id).Distinct()];

    private async ValueTask<string> RootOf(string workspaceId, CancellationToken cancellationToken) =>
        (await context.WorkspaceAsync(workspaceId, cancellationToken))?.Path ?? workspaceId;

    private async ValueTask<ChatHeader> OpenAsync(ChatOpenParams p)
    {
        if (sessions.TryGetValue((p.WorkspaceId, p.ChatId), out var existing) && existing.Active) return existing.Header;
        var settings = context.Settings<PiSettings>();
        var cwd = await RootOf(p.WorkspaceId, CancellationToken.None);
        var record = await Task.Run(() => LoadRecord(p.WorkspaceId, p.ChatId));
        var resume = p.ResumeSessionId ?? record?.SessionFile;
        var command = settings.Command.Trim() is { Length: > 0 } own ? own : "pi";
        // The pinned Pi runs on Bun and never falls back to the system one.
        var all = context.AgentLaunchContributions(PiManifest.Id);
        var pinned = settings.Source == PiSources.Managed && PiLinks.InTab is null ? managed : null;
        // Like the other agents, a custom command is never rewritten: only the plain `pi` is wrapped or blocked by a launch contribution.
        var contributions = pinned is not null || Path.GetFileNameWithoutExtension(command) == "pi" ? all : [];
        var replacing = contributions.FirstOrDefault(contribution => contribution.ReplacesExecutable && contribution.Wrapper.Count > 0)?.Wrapper ?? [];
        var spawn = new PiSpawn(command, cwd)
        {
            Wrapper = replacing,
            Blocked = AgentLaunchComposition.Blocked(contributions) ?? (pinned is not null ? ManagedBlock(contributions) : null),
            Extra = [.. contributions.SelectMany(contribution => contribution.Arguments)],
            Model = p.Model ?? (settings.Model.Length > 0 ? settings.Model : null),
            Thinking = p.Effort ?? (settings.Thinking.Length > 0 ? settings.Thinking : null),
            Codemode = settings.Codemode,
            Session = resume is not null && File.Exists(resume) ? resume : null,
            Environment = ContributedEnvironment(context),
            McpUrl = PiLinks.InTab is null ? McpUrlFor(p.WorkspaceId, p.ChatId) : null
        };
        var session = existing ?? sessions.AddOrUpdate((p.WorkspaceId, p.ChatId),
            _ => new PiChatSession(p.WorkspaceId, p.ChatId, Publish, message => context.Log.Warn(message), (chat, file) => SaveSessionFile(p.WorkspaceId, chat, file)),
            (_, current) => current);
        var header = session.Begin(spawn);
        _ = pinned is null ? session.StartAsync(spawn) : Task.Run(() => StartPinnedAsync(session, spawn, pinned));
        return header;
    }

    // The host's MCP address keyed to this chat, as a Claude Code chat gets it; null (no tools, chat still starts) when the host gives none.
    private string? McpUrlFor(string workspaceId, string chatId)
    {
        try
        {
            var url = context.PublicBaseUrl();
            var route = PluginIdentity.Route(context.Id);
            return $"{(url.EndsWith(route, StringComparison.Ordinal) ? url[..^route.Length] : url)}/mcp/{context.TerminalToken(new TerminalRef(workspaceId, chatId))}";
        }
        catch (InvalidOperationException error)
        {
            context.Log.Warn("the chat gets no tereditor tools: " + error.Message);
            return null;
        }
    }

    // Downloads the pinned Pi when it is not there yet, then runs it on Bun.
    private async Task StartPinnedAsync(PiChatSession session, PiSpawn spawn, PiManaged pinned)
    {
        try
        {
            spawn = await pinned.PrepareAsync(spawn, context.TrackRuntimeProcess);
            await session.StartAsync(spawn);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            session.Fail(pinned.LastError is { Length: > 0 } failure ? failure : error.Message);
        }
    }

    /// <summary>Pi's stored sessions for a directory, newest first: <c>--&lt;path with / as -&gt;--</c> under the sessions root.</summary>
    internal ChatSessionList ListSessions(string cwd)
    {
        var directory = Path.Combine(sessionsRoot(), "--" + cwd.TrimStart('/', '\\').Replace('/', '-').Replace('\\', '-').Replace(':', '-') + "--");
        if (!Directory.Exists(directory)) return new([]);
        var files = new DirectoryInfo(directory).EnumerateFiles("*.jsonl").OrderByDescending(file => file.LastWriteTimeUtc).Take(50);
        var open = sessions.Values.Where(session => session.Active && session.SessionFile is not null).ToDictionary(session => session.SessionFile!, session => session);
        return new([.. files.Select(file => new ChatSessionInfo(file.FullName, TitleOf(file.FullName), file.LastWriteTimeUtc.ToString("o"))
        {
            ChatId = open.TryGetValue(file.FullName, out var live) ? ChatOf(live) : null
        })]);
    }

    private string? ChatOf(PiChatSession session) => sessions.FirstOrDefault(pair => ReferenceEquals(pair.Value, session)).Key.Item2;

    // The session's first user message, or the name the user gave it.
    private static string TitleOf(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(200))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (PiFolder.Str(root, "type") == "session_info" && PiFolder.Str(root, "name") is { Length: > 0 } name) return name;
                if (PiFolder.Str(root, "type") == "message" && root.TryGetProperty("message", out var message) && PiFolder.Str(message, "role") == "user"
                    && PiFolder.UserText(message).Trim() is { Length: > 0 } text) return text.Length > 80 ? text[..80] : text;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private async ValueTask<ChatFileList> FindFilesAsync(ChatFilesParams p, CancellationToken cancellationToken)
    {
        var listing = await context.GitAsync(p.WorkspaceId, ["ls-files", "--cached", "--others", "--exclude-standard"], new GitRunOptions(TimeSpan.FromSeconds(10)), cancellationToken);
        if (!listing.Ok) return new([]);
        var query = p.Query.Trim();
        var paths = listing.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => query.Length == 0 || path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(path => path.Length)
            .Take(50);
        return new([.. paths]);
    }
}

[JsonSerializable(typeof(Dictionary<string, PiChatRecord>))]
internal sealed partial class PiHostJson : JsonSerializerContext;