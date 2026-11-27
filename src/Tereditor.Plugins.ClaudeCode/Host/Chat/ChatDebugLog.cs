using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.ClaudeCode.Host.Chat;

/// <summary>
/// One chat's debug log: the raw lines exchanged with the Claude process, kept in a bounded ring and published in batches. Credentials are hidden
/// in what is kept and sent; the traffic itself is never touched. Memory only: nothing here is written to disk.
/// </summary>
internal sealed class ChatDebugLog(string workspaceId, string chatId, Action<ChatPush> publish, Func<long>? clock = null, Action<Action>? later = null)
{
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    private readonly Lock gate = new();
    private readonly Queue<ChatDebugEntry> ring = new();
    private readonly List<ChatDebugEntry> pending = [];
    private long chars;
    private long sequence;
    private long lastTime;
    private bool scheduled;

    /// <summary>Records one line. Runs on the reader or writer thread of the child, never on the UI.</summary>
    public void Record(ChatDebugDirection direction, string line)
    {
        // Describing a line is slow and the three recording threads overlap in it, so its number and time are given only when it is listed.
        var described = Describe(0, 0, direction, line);
        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (gate)
        {
            lastTime = Math.Max(lastTime, now);
            var entry = described with { Seq = ++sequence, TimeMs = lastTime };
            ring.Enqueue(entry);
            chars += entry.Text.Length;
            while (ring.Count > ChatDebugLimits.MaxEntries || chars > ChatDebugLimits.MaxChars)
                chars -= ring.Dequeue().Text.Length;
            pending.Add(entry);
            // A flood the view cannot keep up with is cut to what the ring would hold anyway.
            if (pending.Count > ChatDebugLimits.MaxEntries) pending.RemoveRange(0, pending.Count - ChatDebugLimits.MaxEntries);
            if (scheduled) return;
            scheduled = true;
        }
        Schedule();
    }

    private void Schedule()
    {
        if (later is not null) later(Flush);
        else _ = Task.Delay(BatchInterval).ContinueWith(_ => Flush(), TaskScheduler.Default);
    }

    private void Flush()
    {
        ChatDebugEntry[] batch;
        lock (gate)
        {
            batch = [.. pending];
            pending.Clear();
        }
        // Scheduled stays set while this batch is delivered: a later batch that overtook it would make the view drop these lines.
        try { if (batch.Length > 0) publish(new ChatDebugPush(workspaceId, chatId, batch)); }
        finally
        {
            bool more;
            lock (gate) more = scheduled = pending.Count > 0;
            if (more) Schedule();
        }
    }

    public IReadOnlyList<ChatDebugEntry> Entries()
    {
        lock (gate) return [.. ring];
    }

    /// <summary>Empties the log; numbering goes on, so a view that still holds older lines never confuses them with new ones.</summary>
    public void Clear()
    {
        lock (gate)
        {
            ring.Clear();
            pending.Clear();
            chars = 0;
        }
    }

    private static readonly Regex Bearer = new(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{6,}", RegexOptions.Compiled);
    private static readonly Regex Known = new(@"\b(?:sk-ant-[A-Za-z0-9_-]{8,}|sk-[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,})", RegexOptions.Compiled);
    private static readonly Regex Assignment = new(@"(?i)\b([A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|PASSWD|API_?KEY|AUTHORIZATION)[A-Z0-9_]*)=\S+", RegexOptions.Compiled);
    internal const string Hidden = "[hidden]";

    internal static ChatDebugEntry Describe(long seq, long timeMs, ChatDebugDirection direction, string line)
    {
        var size = System.Text.Encoding.UTF8.GetByteCount(line);
        if (direction == ChatDebugDirection.Stderr) return new(seq, timeMs, direction, size, "stderr", Trim(Mask(line)), line.Length > ChatDebugLimits.MaxEntryChars ? TooLarge(line.Length) : Mask(line));
        if (line.Length > ChatDebugLimits.MaxEntryChars) return new(seq, timeMs, direction, size, "message", "message (too large to show)", TooLarge(line.Length));
        try
        {
            if (JsonNode.Parse(line) is { } node)
            {
                var (type, summary) = Summarize(node);
                Redact(node);
                return new(seq, timeMs, direction, size, type, summary, node.ToJsonString(Pretty));
            }
        }
        catch (JsonException) { }
        return new(seq, timeMs, direction, size, "text", Trim(Mask(line)), Mask(line));
    }

    private static string TooLarge(int length) => $"[{length:N0} characters; too large to show]";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Trim(string text)
    {
        var one = text.ReplaceLineEndings(" ");
        return one.Length > 160 ? one[..160] + "..." : one;
    }

    /// <summary>The type (<c>stream_event</c>, <c>assistant</c>, <c>control_request</c> ...) and a one-line summary: type, subtype and what the message carries.</summary>
    internal static (string Type, string Summary) Summarize(JsonNode node)
    {
        if (node is not JsonObject obj) return ("message", "message");
        var type = Text(obj["type"]) ?? "message";
        var detail = type switch
        {
            "stream_event" => Text(Get(obj["event"], "type")),
            "control_request" => Text(Get(obj["request"], "subtype")) is { } sub ? sub + (Text(Get(obj["request"], "tool_name")) is { } tool ? " " + tool : "") : null,
            "control_response" => Text(Get(obj["response"], "subtype")),
            _ => Text(obj["subtype"])
        };
        var summary = detail is null ? type : type + "/" + detail;
        if (type is "assistant" or "user" && Get(obj["message"], "content") is JsonArray blocks)
        {
            var parts = new List<string>();
            foreach (var block in blocks.OfType<JsonObject>())
                parts.Add(Text(block["type"]) switch
                {
                    "tool_use" => "tool_use " + Text(block["name"]),
                    "tool_result" => "tool_result",
                    "text" => "text: " + Trim(Mask(Text(block["text"]) ?? "")),
                    var other => other ?? "block"
                });
            if (parts.Count > 0) summary += " (" + string.Join(", ", parts.Take(3)) + (parts.Count > 3 ? ", ..." : "") + ")";
        }
        else if (type == "user" && Text(Get(obj["message"], "content")) is { } prompt) summary += " (text: " + Trim(Mask(prompt)) + ")";
        return (type, summary.Length > 200 ? summary[..200] + "..." : summary);
    }

    private static JsonNode? Get(JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A key whose value is a credential: token, API key, authorization, password, secret. Counts such as <c>max_tokens</c> are not.</summary>
    internal static bool Sensitive(string key)
    {
        var name = key.Replace("_", "").Replace("-", "").ToLowerInvariant();
        return name.EndsWith("token", StringComparison.Ordinal) || name.EndsWith("apikey", StringComparison.Ordinal) || name.EndsWith("authorization", StringComparison.Ordinal)
            || name.EndsWith("password", StringComparison.Ordinal) || name.EndsWith("passwd", StringComparison.Ordinal) || name.EndsWith("secret", StringComparison.Ordinal)
            || name is "credentials" or "credential" or "cookie" or "setcookie";
    }

    internal static string Mask(string text)
    {
        if (text.Length == 0) return text;
        text = Bearer.Replace(text, match => match.Groups[1].Value + " " + Hidden);
        text = Assignment.Replace(text, match => match.Groups[1].Value + "=" + Hidden);
        return Known.Replace(text, Hidden);
    }

    // Hides credential keys' values and masks credential-looking text in every string, in place.
    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToArray())
                {
                    if (value is null) continue;
                    if (Sensitive(key) && !(value is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False)) obj[key] = Hidden;
                    else if (Text(value) is { } text) obj[key] = Mask(text);
                    else Redact(value);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    if (Text(array[index]) is { } text) array[index] = Mask(text);
                    else Redact(array[index]);
                break;
        }
    }
}