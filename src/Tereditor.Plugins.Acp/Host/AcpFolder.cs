using System.Text;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>
/// Folds ACP <c>session/update</c> payloads into the chat's ordered, id-keyed item list. Pure state, no process and no publishing, so the
/// checks drive it with recorded updates. Not thread-safe; the session serializes calls.
/// </summary>
internal sealed class AcpFolder
{
    private readonly List<ChatItem> items = [];
    private readonly Dictionary<string, int> index = [];
    private readonly Dictionary<string, string> full = [];
    private readonly HashSet<string> changed = [];
    private long seq;
    private int counter;
    private bool reset;
    private bool headerChanged;
    // The id of the message or thought still growing; any other kind of update ends it.
    private string? streaming;

    public ChatHeader Header { get; private set; } = new(ChatStatus.Starting);

    public IReadOnlyList<ChatItem> Items => items;

    public string? Detail(string id) => full.GetValueOrDefault(id);

    public (IReadOnlyList<ChatItem> Items, bool Reset, bool Header) Drain()
    {
        IReadOnlyList<ChatItem> drained = [.. changed.Select(id => items[index[id]]).OrderBy(item => item.Seq)];
        var result = (drained, reset, headerChanged);
        changed.Clear();
        reset = false;
        headerChanged = false;
        return result;
    }

    public void Update(Func<ChatHeader, ChatHeader> edit)
    {
        Header = edit(Header);
        headerChanged = true;
    }

    public void SetStatus(ChatStatus status, string? error = null) =>
        Update(header => header with { Status = status, Error = error ?? (status is ChatStatus.Failed or ChatStatus.Exited ? header.Error : null), RuntimeOffer = status == ChatStatus.Failed ? header.RuntimeOffer : null });

    /// <summary>Drops the conversation (a session/load replays it from the start).</summary>
    public void Clear()
    {
        items.Clear();
        index.Clear();
        full.Clear();
        changed.Clear();
        streaming = null;
        reset = true;
    }

    private void Put(ChatItem item)
    {
        if (index.TryGetValue(item.Id, out var at)) items[at] = item;
        else
        {
            index[item.Id] = items.Count;
            items.Add(item);
        }
        changed.Add(item.Id);
    }

    private T? Get<T>(string id) where T : ChatItem => index.TryGetValue(id, out var at) ? items[at] as T : null;

    private long Next() => ++seq;

    public ChatUserItem AddUser(string text, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        CloseStreaming();
        var item = new ChatUserItem("u" + ++counter, Next(), text) { Attachments = attachments is { Count: > 0 } ? attachments : null };
        Put(item);
        return item;
    }

    public void Notice(ChatNoticeLevel level, string text, string? category = null, bool? retryable = null, ChatNoticeAction? action = null, IReadOnlyList<ChatNoticeRequest>? requests = null)
    {
        CloseStreaming();
        Put(new ChatNoticeItem("n" + ++counter, Next(), level, text) { Category = category, Retryable = retryable, Action = action, Requests = requests ?? [] });
    }

    /// <summary>
    /// The turn is over and these calls never reported an end (a cancelled turn, a failed one): they stop showing as running, a subagent's included.
    /// </summary>
    public void SettleOpenTools()
    {
        foreach (var tool in items.OfType<ChatToolItem>().Where(tool => tool.State is ChatToolState.Pending or ChatToolState.Running).ToList())
            Put(tool with { State = ChatToolState.Failed });
    }

    /// <summary>Ends the growing message or thought so the next chunk starts a new item.</summary>
    public void CloseStreaming()
    {
        if (streaming is null) return;
        switch (Get<ChatItem>(streaming))
        {
            case ChatAssistantItem assistant: Put(assistant with { Streaming = false }); break;
            case ChatThinkingItem thinking: Put(thinking with { Streaming = false }); break;
        }
        streaming = null;
    }

    public static string Str(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>The text of a content block; other blocks read as a short description.</summary>
    public static string BlockText(JsonNode? block) => Str(block, "type") switch
    {
        "text" => Str(block, "text"),
        "image" => "[image]",
        "audio" => "[audio]",
        "resource_link" => "[" + (Str(block, "title") is { Length: > 0 } title ? title : Str(block, "name")) + "](" + Str(block, "uri") + ")",
        "resource" => Str(block?["resource"], "text") is { Length: > 0 } text ? text : "[" + Str(block?["resource"], "uri") + "]",
        _ => ""
    };

    /// <summary>Applies one <c>update</c> object. Returns false for a kind this host does not fold.</summary>
    public bool Apply(JsonObject update)
    {
        switch (Str(update, "sessionUpdate"))
        {
            case "user_message_chunk":
                Chunk(update, user: true);
                return true;
            case "agent_message_chunk":
                Chunk(update, thought: false);
                return true;
            case "agent_thought_chunk":
                Chunk(update, thought: true);
                return true;
            case "tool_call":
            case "tool_call_update":
                Tool(update);
                return true;
            case "plan":
                Plan(update);
                return true;
            case "available_commands_update":
                Update(header => header with
                {
                    // The composer adds the slash itself; an agent that sends "/web" must not list "//web". A nameless entry cannot be run.
                    Commands = [.. (update["availableCommands"] as JsonArray ?? []).OfType<JsonObject>().Select(command => (Name: Str(command, "name").TrimStart('/'), Command: command)).Where(entry => entry.Name.Length > 0).Select(entry =>
                        new ChatCommand(entry.Name, Str(entry.Command, "description") is { Length: > 0 } text ? text : null) { ArgumentHint = Str(entry.Command["input"], "hint") is { Length: > 0 } hint ? hint : null })]
                });
                return true;
            case "current_mode_update":
                Update(header => header with { PermissionMode = Str(update, "currentModeId") });
                return true;
            case "config_option_update":
                ApplyConfig(update["configOptions"] as JsonArray);
                return true;
            case "session_info_update":
                if (Str(update, "title") is { Length: > 0 } title) Update(header => header with { Title = title });
                return true;
            case "usage_update":
                Update(header => header with
                {
                    Usage = header.Usage with
                    {
                        ContextTokens = Number(update["used"]) is { } used ? (long)used : header.Usage.ContextTokens,
                        ContextWindow = Number(update["size"]) is { } size ? (long)size : header.Usage.ContextWindow,
                        CostUsd = update["cost"] is JsonObject { } cost && Number(cost["amount"]) is { } amount ? amount : header.Usage.CostUsd
                    }
                });
                return true;
            default:
                return false;
        }
    }

    /// <summary>A JSON number as a double, whether the agent wrote it as an integer or with a fraction; null for anything else.</summary>
    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    /// <summary>
    /// The subagent call an update happened inside. ACP version 1 has no subagent record; Claude Code's adapter stamps every update of a subagent
    /// with <c>_meta.claudeCode.parentToolUseId</c>, the id of the Task or Agent tool call. The item id of that call is "t" + the id.
    /// </summary>
    private static string? ParentOf(JsonObject update) =>
        Str((update["_meta"] as JsonObject)?["claudeCode"], "parentToolUseId") is { Length: > 0 } parent ? "t" + parent : null;

    private static bool StartsSubagent(string? name) => name is "Task" or "Agent";

    private void Chunk(JsonObject update, bool user = false, bool thought = false)
    {
        var text = BlockText(update["content"]);
        if (user)
        {
            // A replayed user turn arrives in pieces; they join while no other kind of item came between.
            if (streaming is { } open && Get<ChatUserItem>(open) is { } current) { Put(current with { Text = current.Text + text }); return; }
            var item = AddUser(text);
            streaming = item.Id;
            return;
        }
        var parent = ParentOf(update);
        if (streaming is { } id)
        {
            // A subagent's words and its parent's can interleave; a chunk only grows the item of its own thread.
            if (!thought && Get<ChatAssistantItem>(id) is { } message && message.ParentId == parent) { Put(message with { Text = message.Text + text }); return; }
            if (thought && Get<ChatThinkingItem>(id) is { } thinking && thinking.ParentId == parent) { Put(thinking with { Text = thinking.Text + text }); return; }
        }
        CloseStreaming();
        var next = "a" + ++counter;
        Put(thought ? new ChatThinkingItem(next, Next(), text) { Streaming = true, ParentId = parent } : new ChatAssistantItem(next, Next(), text) { Streaming = true, ParentId = parent });
        streaming = next;
    }

    private void Plan(JsonObject update)
    {
        CloseStreaming();
        var text = new StringBuilder("**Plan**\n\n");
        foreach (var entry in (update["entries"] as JsonArray ?? []).OfType<JsonObject>())
            text.Append(Str(entry, "status") switch { "completed" => "- [x] ", "in_progress" => "- [~] ", _ => "- [ ] " }).AppendLine(Str(entry, "content"));
        // A subagent's checklist is its own item inside its thread, so it never replaces the parent's plan.
        var parent = ParentOf(update);
        var planId = parent is null ? "plan" : "plan:" + parent;
        var existing = Get<ChatAssistantItem>(planId);
        Put(existing is null ? new ChatAssistantItem(planId, Next(), text.ToString()) { ParentId = parent } : existing with { Text = text.ToString() });
    }

    private static ChatToolState StateOf(string status) => status switch
    {
        "in_progress" => ChatToolState.Running,
        "completed" => ChatToolState.Done,
        "failed" => ChatToolState.Failed,
        _ => ChatToolState.Pending
    };

    private void Tool(JsonObject update)
    {
        CloseStreaming();
        var callId = Str(update, "toolCallId");
        var id = "t" + callId;
        var existing = Get<ChatToolItem>(id);
        var meta = (update["_meta"] as JsonObject)?["claudeCode"];
        // The adapter names the tool in its meta. A subagent call keeps that name (the view nests and opens its thread by it) and its title is the summary.
        var subagent = StartsSubagent(Str(meta, "toolName")) ? Str(meta, "toolName") : StartsSubagent(existing?.Name) ? existing!.Name : null;
        var name = subagent ?? (Str(update, "title") is { Length: > 0 } title ? title : existing?.Name ?? (Str(update, "kind") is { Length: > 0 } kind ? kind : "Tool"));
        var state = update.ContainsKey("status") ? StateOf(Str(update, "status")) : existing?.State ?? ChatToolState.Pending;
        var path = (update["locations"] as JsonArray)?.OfType<JsonObject>().Select(location => Str(location, "path")).FirstOrDefault(p => p.Length > 0) ?? existing?.FilePath;
        string? output = existing?.Output;
        if (update["content"] is JsonArray content)
        {
            var text = new StringBuilder();
            foreach (var block in content.OfType<JsonObject>())
            {
                switch (Str(block, "type"))
                {
                    case "content": text.AppendLine(BlockText(block["content"])); break;
                    case "diff":
                        path = Str(block, "path") is { Length: > 0 } diffPath ? diffPath : path;
                        text.AppendLine("--- " + Str(block, "path")).AppendLine("+++ " + Str(block, "path"));
                        foreach (var line in Str(block, "oldText").Split('\n').Where(l => l.Length > 0)) text.Append('-').AppendLine(line);
                        foreach (var line in Str(block, "newText").Split('\n').Where(l => l.Length > 0)) text.Append('+').AppendLine(line);
                        break;
                    case "terminal": text.AppendLine("[terminal " + Str(block, "terminalId") + "]"); break;
                }
            }
            output = text.ToString().TrimEnd();
        }
        var input = update["rawInput"] is { } raw ? AcpLocalJson.Input(raw) : existing?.Input ?? AcpLocalJson.Input(null);
        var truncated = output is { } o && o.Length > ChatContract.OutputLimit;
        if (truncated) full[id] = output!;
        var summary = existing?.Summary ?? (subagent is not null && Str(update, "title") is { Length: > 0 } described ? described : path is { Length: > 0 } ? path : null);
        Put(new ChatToolItem(id, existing?.Seq ?? Next(), name, input, state) { ParentId = ParentOf(update) ?? existing?.ParentId, Summary = summary, Output = truncated ? output![..ChatContract.OutputLimit] : output, OutputTruncated = truncated, FilePath = path });
    }

    /// <summary>The id of the config option that sets the model, or null when the agent lists its models through <c>models</c> (set by <c>session/set_model</c>).</summary>
    public string? ModelConfigId { get; private set; }

    /// <summary>The id of the config option that sets the mode, or null when the agent lists its modes through <c>modes</c> (set by <c>session/set_mode</c>).</summary>
    public string? ModeConfigId { get; private set; }

    /// <summary>Whether a config option with this id is a boolean (set with a JSON boolean).</summary>
    public bool IsBoolean(string configId) => Header.Options.FirstOrDefault(option => option.Id == configId)?.Kind == "boolean";

    private static string CategoryOf(JsonObject option) => Str(option, "category") is { Length: > 0 } category ? category : Str(option, "id") switch
    {
        "model" => "model",
        "mode" => "mode",
        "thought_level" => "thought_level",
        _ => ""
    };

    /// <summary>The values of a select option, flat or grouped (<c>[{group, name, options}]</c>); a group's name goes to <see cref="ChatOptionValue.Group"/>.</summary>
    private static List<ChatOptionValue> Values(JsonArray? options)
    {
        var values = new List<ChatOptionValue>();
        foreach (var entry in (options ?? []).OfType<JsonObject>())
        {
            if (entry["options"] is JsonArray inner)
            {
                var group = Str(entry, "name") is { Length: > 0 } name ? name : Str(entry, "group");
                values.AddRange(inner.OfType<JsonObject>().Where(value => value.ContainsKey("value")).Select(value => Value(value, group.Length > 0 ? group : null)));
            }
            else if (entry.ContainsKey("value")) values.Add(Value(entry, null));
        }
        return values;
    }

    private static ChatOptionValue Value(JsonObject value, string? group) =>
        new(Str(value, "value"), Str(value, "name") is { Length: > 0 } label ? label : Str(value, "value")) { Description = Str(value, "description") is { Length: > 0 } d ? d : null, Group = group };

    /// <summary>
    /// The agent's session options, which replace <c>modes</c> and <c>models</c> for the same purpose. The option that sets the model and the one that
    /// sets the mode fill the header's model and mode lists (an option with no category is told by its id); every other select or boolean option
    /// is listed as an option of its own (thought level, model parameters, anything custom).
    /// </summary>
    public void ApplyConfig(JsonArray? options)
    {
        if (options is null) return;
        var others = new List<ChatOption>();
        foreach (var option in options.OfType<JsonObject>())
        {
            var id = Str(option, "id");
            if (id.Length == 0) continue;
            var label = Str(option, "name") is { Length: > 0 } name ? name : id;
            var description = Str(option, "description") is { Length: > 0 } d ? d : null;
            var category = CategoryOf(option);
            if (Str(option, "type") == "boolean")
            {
                var on = option["currentValue"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
                others.Add(new ChatOption(id, label, "boolean", on ? "true" : "false") { Description = description, Category = category.Length > 0 ? category : null });
                continue;
            }
            var values = Values(option["options"] as JsonArray);
            var current = Str(option, "currentValue");
            switch (category)
            {
                case "model":
                    ModelConfigId = id;
                    Update(header => header with
                    {
                        Model = current,
                        Models = [.. values.Select(value => new ChatModel(value.Value, value.Label) { Description = value.Description, Provider = value.Group })]
                    });
                    break;
                case "mode":
                    ModeConfigId = id;
                    Update(header => header with
                    {
                        PermissionMode = current,
                        PermissionModes = [.. values.Select(value => new ChatMode(value.Value, value.Label, value.Description ?? ""))]
                    });
                    break;
                default:
                    others.Add(new ChatOption(id, label, "select", current) { Description = description, Category = category.Length > 0 ? category : null, Values = values });
                    break;
            }
        }
        Update(header => header with { Options = others });
    }

    /// <summary>An agent that confirmed a change without sending the new options: the value the user chose is the current one.</summary>
    public void SetCurrent(string configId, string value)
    {
        if (configId == ModelConfigId) Update(header => header with { Model = value });
        else if (configId == ModeConfigId) Update(header => header with { PermissionMode = value });
        else Update(header => header with { Options = [.. header.Options.Select(option => option.Id == configId ? option with { Current = value } : option)] });
    }

    /// <summary>Modes from <c>session/new</c>: <c>{currentModeId, availableModes: [{id, name, description}]}</c>.</summary>
    public void ApplyModes(JsonObject? modes)
    {
        if (modes is null) return;
        Update(header => header with
        {
            PermissionMode = Str(modes, "currentModeId"),
            PermissionModes = [.. (modes["availableModes"] as JsonArray ?? []).OfType<JsonObject>().Select(mode => new ChatMode(Str(mode, "id"), Str(mode, "name") is { Length: > 0 } name ? name : Str(mode, "id"), Str(mode, "description")))]
        });
    }

    /// <summary>Models from <c>session/new</c> (<c>{currentModelId, availableModels: [{modelId, name}]}</c>).</summary>
    public void ApplyModels(JsonObject? models)
    {
        if (models is null) return;
        static string IdOf(JsonObject model) => Str(model, "modelId") is { Length: > 0 } id ? id : Str(model, "id");
        Update(header => header with
        {
            Model = Str(models, "currentModelId"),
            Models = [.. (models["availableModels"] as JsonArray ?? []).OfType<JsonObject>().Select(model =>
                new ChatModel(IdOf(model), Str(model, "name") is { Length: > 0 } name ? name : IdOf(model)) { Description = Str(model, "description") is { Length: > 0 } d ? d : null })]
        });
    }

    /// <summary>A permission request as the card shows it: the tool's title and kind, the files, the diffs it would apply and the agent's own answers.</summary>
    public ChatPermissionItem AddPermission(string requestId, JsonObject toolCall, IReadOnlyList<AcpPermissionOption> options)
    {
        CloseStreaming();
        var title = Str(toolCall, "title");
        var always = options.FirstOrDefault(option => option.Kind == "allow_always");
        var locations = (toolCall["locations"] as JsonArray ?? []).OfType<JsonObject>().Where(location => Str(location, "path").Length > 0)
            .Select(location => Str(location, "path") + (location["line"] is JsonValue line && line.TryGetValue<int>(out var number) ? ":" + number : "")).ToList();
        var diffs = new List<ChatPermissionDiff>();
        var notes = new StringBuilder();
        foreach (var block in (toolCall["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (Str(block, "type"))
            {
                case "diff":
                    diffs.Add(new ChatPermissionDiff(Str(block, "path"), block["oldText"] is JsonValue old && old.TryGetValue<string>(out var oldText) ? oldText : null, Str(block, "newText")));
                    break;
                case "content" when BlockText(block["content"]) is { Length: > 0 } text:
                    notes.AppendLine(text);
                    break;
            }
        }
        var item = new ChatPermissionItem("p" + ++counter, Next(), requestId, title.Length > 0 ? title : Str(toolCall, "kind"), AcpLocalJson.Input(toolCall["rawInput"]))
        {
            Title = title,
            Description = notes.Length > 0 ? notes.ToString().TrimEnd() : null,
            Prompt = title.Length > 0 ? $"Allow \"{title}\"?" : "Allow this action?",
            AlwaysLabel = always?.Name,
            Decision = ChatPermissionDecision.Pending,
            ToolKind = Str(toolCall, "kind") is { Length: > 0 } kind ? kind : null,
            Locations = locations,
            Diffs = diffs,
            Options = [.. options.Select(option => new ChatPermissionOption(option.Id, option.Name, option.Kind))]
        };
        Put(item);
        return item;
    }

    public void Settle(string requestId, ChatPermissionDecision decision)
    {
        var item = items.OfType<ChatPermissionItem>().FirstOrDefault(p => p.RequestId == requestId);
        if (item is not null) Put(item with { Decision = decision });
    }
}

/// <summary>One choice of a permission request: the agent's option id and label, and its kind (allow_once, allow_always, reject_once, reject_always).</summary>
internal sealed record AcpPermissionOption(string Id, string Name, string Kind);