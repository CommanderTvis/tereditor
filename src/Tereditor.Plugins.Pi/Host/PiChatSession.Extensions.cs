using System.Text.Json;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>
/// Pi's extension UI sub-protocol (docs/rpc-extension-ui.md). A dialog (select, confirm, input, editor) becomes a request card in the
/// chat and is answered with an <c>extension_ui_response</c> by its id; the other methods become notices, status chips, widget blocks,
/// the chat title and the composer text. A dialog with a timeout is settled by the host when it runs out; Pi has already resolved it.
/// </summary>
internal sealed partial class PiChatSession
{
    /// <summary>An open dialog: its card, how its answer is written, and (for select and confirm) the offered values.</summary>
    private sealed record OpenDialog(string ItemId, string Method, string Title, IReadOnlyList<string> Options);

    private readonly Dictionary<string, OpenDialog> dialogs = [];
    private string draftText = "";
    private long draftRevision;
    private string? draftOrigin;

    private ChatDraft DraftNow()
    {
        lock (gate) return new ChatDraft(draftText, draftRevision) { Origin = draftOrigin };
    }

    public ChatDraft SetDraft(string text, string? clientId)
    {
        lock (gate)
        {
            if (text != draftText) { draftText = text; draftRevision++; draftOrigin = clientId; PublishDraft(); }
            return DraftNow();
        }
    }

    private void ClearDraft(string? clientId)
    {
        lock (gate)
        {
            if (draftText.Length == 0) return;
            draftText = "";
            draftRevision++;
            draftOrigin = clientId;
            PublishDraft();
        }
    }

    private void PublishDraft() => publish(new ChatDraftPush(workspaceId, chatId, new ChatDraft(draftText, draftRevision) { Origin = draftOrigin }));

    private void ExtensionRequest(JsonElement request)
    {
        var method = PiFolder.Str(request, "method");
        var id = PiFolder.Str(request, "id");
        switch (method)
        {
            case "select" or "confirm" or "input" or "editor":
                if (id.Length > 0) OpenDialogCard(request, id, method);
                return;
            case "notify":
                lock (gate)
                {
                    folder.Notice(PiFolder.Str(request, "notifyType") switch { "error" => ChatNoticeLevel.Error, "warning" => ChatNoticeLevel.Warning, _ => ChatNoticeLevel.Info }, PiFolder.Str(request, "message"));
                    ScheduleFlush();
                }
                return;
            case "setStatus":
                lock (gate)
                {
                    var key = PiFolder.Str(request, "statusKey");
                    var text = PiFolder.Str(request, "statusText");
                    folder.Update(header => header with { Statuses = [.. header.Statuses.Where(status => status.Key != key), .. text.Length > 0 ? [new ChatExtensionStatus(key, text)] : Array.Empty<ChatExtensionStatus>()] });
                    ScheduleFlush();
                }
                return;
            case "setWidget":
                lock (gate)
                {
                    var key = PiFolder.Str(request, "widgetKey");
                    var lines = request.TryGetProperty("widgetLines", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(line => line.GetString() ?? "").ToArray() : null;
                    var below = PiFolder.Str(request, "widgetPlacement") == "belowEditor";
                    folder.Update(header => header with { Widgets = [.. header.Widgets.Where(widget => widget.Key != key), .. lines is { Length: > 0 } ? [new ChatWidget(key, lines, below)] : Array.Empty<ChatWidget>()] });
                    ScheduleFlush();
                }
                return;
            case "setTitle":
                lock (gate)
                {
                    var title = PiFolder.Str(request, "title");
                    folder.Update(header => header with { Title = title.Length > 0 ? title : header.Title });
                    ScheduleFlush();
                }
                return;
            case "set_editor_text":
                lock (gate)
                {
                    var text = PiFolder.Str(request, "text");
                    if (text != draftText) { draftText = text; draftRevision++; draftOrigin = "extension"; PublishDraft(); }
                }
                return;
            default:
                // The docs define no reply for a method they do not list.
                log("Pi sent an extension request this host does not know: " + method);
                return;
        }
    }

    private void OpenDialogCard(JsonElement request, string id, string method)
    {
        var title = PiFolder.Str(request, "title");
        var itemId = "x" + id;
        IReadOnlyList<string> options = [];
        lock (gate)
        {
            switch (method)
            {
                case "select":
                    options = request.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray().Select(option => option.GetString() ?? "")] : [];
                    folder.Show(seq => Card(itemId, seq, id, title, null, [.. options.Select((option, at) => new ChatPermissionOption(at.ToString(), option, Refusal(option) ? "reject_once" : "allow_once"))]));
                    break;
                case "confirm":
                    options = ["Yes", "No"];
                    folder.Show(seq => Card(itemId, seq, id, title, PiFolder.Str(request, "message") is { Length: > 0 } message ? message : null, [new("yes", "Yes", "allow_once"), new("no", "No", "reject_once")]));
                    break;
                default:
                    folder.Show(seq => new ChatQuestionItem(itemId, seq, id, [new ChatQuestion(title, [])
                    {
                        Placeholder = PiFolder.Str(request, "placeholder") is { Length: > 0 } hint ? hint : null,
                        Prefill = PiFolder.Str(request, "prefill") is { Length: > 0 } prefill ? prefill : null,
                        Multiline = method == "editor"
                    }]));
                    break;
            }
            dialogs[id] = new OpenDialog(itemId, method, title, options);
            ScheduleFlush();
        }
        if (request.TryGetProperty("timeout", out var timeout) && timeout.ValueKind == JsonValueKind.Number && timeout.TryGetInt32(out var milliseconds) && milliseconds > 0)
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(milliseconds, lifetime.Token); }
                catch (OperationCanceledException) { return; }
                // Pi resolves a dialog that ran out of time on its own: nothing is sent.
                if (SettleDialogs(id, "no-answer")) lock (gate) { folder.Notice(ChatNoticeLevel.Info, $"A request from an extension timed out: {title}"); ScheduleFlush(); }
            });
    }

    private static ChatPermissionItem Card(string itemId, long seq, string requestId, string title, string? message, IReadOnlyList<ChatPermissionOption> options) =>
        new(itemId, seq, requestId, "extension", JsonDocument.Parse("{}").RootElement.Clone()) { Prompt = title, Description = message, Options = options };

    private static bool Refusal(string option)
    {
        var text = option.Trim().ToLowerInvariant();
        return text is "no" or "cancel" or "block" or "deny" or "reject" || text.StartsWith("no,", StringComparison.Ordinal) || text.StartsWith("don't", StringComparison.Ordinal) || text.StartsWith("block", StringComparison.Ordinal) || text.StartsWith("deny", StringComparison.Ordinal);
    }

    /// <summary>Marks open dialogs as over (all of them when id is null) without writing to Pi; false when none was open.</summary>
    private bool SettleDialogs(string? id, string outcome)
    {
        lock (gate)
        {
            var taken = id is null ? dialogs.ToArray() : dialogs.Remove(id, out var one) ? [new KeyValuePair<string, OpenDialog>(id, one)] : [];
            if (id is null) dialogs.Clear();
            foreach (var (_, dialog) in taken) Settle(dialog, ChatPermissionDecision.Denied, outcome, null);
            if (taken.Length > 0) ScheduleFlush();
            return taken.Length > 0;
        }
    }

    private void Settle(OpenDialog dialog, ChatPermissionDecision decision, string outcome, IReadOnlyDictionary<string, string>? answers)
    {
        folder.Edit<ChatPermissionItem>(dialog.ItemId, item => item with { Decision = decision });
        folder.Edit<ChatQuestionItem>(dialog.ItemId, item => item with { Decision = decision, Outcome = outcome, Answers = answers });
    }

    private OpenDialog TakeDialog(string requestId)
    {
        lock (gate) return dialogs.Remove(requestId, out var dialog) ? dialog : throw new InvalidOperationException("That request from the extension is no longer open.");
    }

    /// <summary>The answer to a select or confirm card: the chosen option, or a cancellation when none was chosen (Esc).</summary>
    public async Task AnswerAsync(string requestId, string? optionId)
    {
        var dialog = TakeDialog(requestId);
        var reply = new JsonObject { ["type"] = "extension_ui_response", ["id"] = requestId };
        var decision = ChatPermissionDecision.Denied;
        if (optionId is null) reply["cancelled"] = true;
        else if (dialog.Method == "confirm")
        {
            reply["confirmed"] = optionId == "yes";
            decision = optionId == "yes" ? ChatPermissionDecision.Allowed : ChatPermissionDecision.Denied;
        }
        else if (int.TryParse(optionId, out var at) && at >= 0 && at < dialog.Options.Count)
        {
            reply["value"] = dialog.Options[at];
            decision = Refusal(dialog.Options[at]) ? ChatPermissionDecision.Denied : ChatPermissionDecision.Allowed;
        }
        else reply["cancelled"] = true;
        lock (gate) { Settle(dialog, decision, "answered", null); ScheduleFlush(); }
        await TrySendAsync(reply);
    }

    /// <summary>The answer to an input or editor card; an empty text is still a value.</summary>
    public async Task AnswerQuestionAsync(string requestId, IReadOnlyDictionary<string, string>? answers, bool cancelled)
    {
        var dialog = TakeDialog(requestId);
        var reply = new JsonObject { ["type"] = "extension_ui_response", ["id"] = requestId };
        if (cancelled) reply["cancelled"] = true;
        else reply["value"] = answers is not null && answers.TryGetValue(dialog.Title, out var text) ? text : "";
        lock (gate) { Settle(dialog, cancelled ? ChatPermissionDecision.Denied : ChatPermissionDecision.Allowed, cancelled ? "declined" : "answered", cancelled ? null : answers); ScheduleFlush(); }
        await TrySendAsync(reply);
    }

    /// <summary>Closing the chat cancels every open dialog, so the extension does not wait for a user who is gone.</summary>
    private async Task CancelDialogsAsync()
    {
        string[] open;
        lock (gate) open = [.. dialogs.Keys];
        foreach (var id in open)
        {
            if (!SettleDialogs(id, "session-ended")) continue;
            await TrySendAsync(new JsonObject { ["type"] = "extension_ui_response", ["id"] = id, ["cancelled"] = true });
        }
    }
}
