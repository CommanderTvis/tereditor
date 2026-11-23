using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

using Tereditor.Plugins.Api;

namespace Tereditor.Host.Core;

/// <summary>One line of the guard's event log: what happened, in which terminal (name and workspace as they were) and when.</summary>
internal sealed record GuardLogEntry(DateTimeOffset At, string Kind, string Terminal, string? Workspace, string Text);

/// <summary>
/// What the memory guard did, kept past a restart or a closed terminal: one JSON object per line at <c>~/.tereditor/logs/memory-guard.log</c>
/// (about 256 KB, then moved to <c>.1</c>, one copy kept). It holds the event text the notification shows (program name, pid, size, reason),
/// the terminal's name and its workspace; never a command line or an environment. Writes are synchronous on the guard's own thread and
/// never throw.
/// </summary>
internal static class GuardEventLog
{
    internal const long Limit = 256 * 1024;
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Lock Gate = new();
    private static string? path;

    /// <summary>The log file; checks point it at a temporary file.</summary>
    internal static string Path
    {
        get => path ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "logs", "memory-guard.log");
        set { lock (Gate) path = value; }
    }

    internal static void Append(GuardEvent item, string terminal, string? workspace)
    {
        if (OperatingSystem.IsBrowser()) return;
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            writer.WriteString("at", item.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("kind", item.Kind.ToString());
            writer.WriteString("terminal", terminal);
            if (workspace is not null) writer.WriteString("workspace", workspace);
            writer.WriteString("text", item.Text);
            writer.WriteEndObject();
        }
        var line = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
        lock (Gate)
            try
            {
                var target = Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                if (new FileInfo(target) is { Exists: true, Length: > Limit }) File.Move(target, target + ".1", true);
                File.AppendAllText(target, line);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The newest <paramref name="max"/> entries, oldest first, from the rotated copy and the current file; empty when there is no log.</summary>
    internal static IReadOnlyList<GuardLogEntry> Recent(int max)
    {
        var entries = new List<GuardLogEntry>();
        lock (Gate)
            foreach (var file in new[] { Path + ".1", Path })
                try
                {
                    if (!File.Exists(file)) continue;
                    foreach (var line in File.ReadLines(file))
                        if (Parse(line) is { } entry) entries.Add(entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return entries.Count <= max ? entries : entries.GetRange(entries.Count - max, max);
    }

    private static GuardLogEntry? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return DateTimeOffset.TryParse(Text("at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && Text("kind") is { } kind && Text("text") is { } text
                ? new(at, kind, Text("terminal") ?? "", Text("workspace"), text) : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>One live terminal as the guard last saw it: its name, its tab when attached to one, and its reading (null before the first sample).</summary>
internal readonly record struct GuardTerminal(string Name, TerminalRef? Tab, GuardUsage? Usage);

/// <summary>
/// The memory guard's state as an agent reads it (the Processes plugin's <c>processes_status</c> tool): the switch and effective limits, each terminal
/// of this host against its limit, and the recent events. Only terminals tereditor started are named; no process is listed except each terminal's
/// largest, by program name, and no command line or environment appears.
/// </summary>
internal static class TerminalMemoryReport
{
    internal const int MaxEvents = TerminalMemoryGuard.MaxEvents;

    /// <param name="on">Whether the guard is on now.</param>
    /// <param name="limits">The guard's limits, read when it is on.</param>
    /// <param name="physical">Physical memory in bytes, 0 when unknown.</param>
    /// <param name="terminals">The host's live terminals.</param>
    /// <param name="events">Recent events, oldest first.</param>
    /// <param name="workspace">The caller's workspace, so its terminals are marked.</param>
    internal static string Build(bool on, TerminalMemoryLimits limits, long physical, IReadOnlyList<GuardTerminal> terminals, IReadOnlyList<GuardLogEntry> events, string? workspace)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("guard");
            writer.WriteBoolean("on", on);
            if (physical > 0) writer.WriteString("physical", TerminalMemoryGuard.Size(physical));
            if (on)
            {
                writer.WriteString("perTerminalLimit", TerminalMemoryGuard.Size(limits.PerTerminalBytes(physical)));
                writer.WriteString("totalLimit", TerminalMemoryGuard.Size(limits.TotalBytes(physical)));
                writer.WriteNumber("graceSeconds", limits.GraceSeconds);
                writer.WriteStartArray("protect");
                foreach (var name in limits.Protect) writer.WriteStringValue(name);
                writer.WriteEndArray();
                writer.WriteString("terminalsUse", TerminalMemoryGuard.Size(terminals.Sum(item => item.Usage?.Bytes ?? 0)));
            }
            writer.WriteEndObject();
            if (workspace is not null) writer.WriteString("workspace", workspace);
            writer.WriteStartArray("terminals");
            if (on)
                foreach (var (name, tab, usage) in terminals)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    if (tab is not null) writer.WriteString("workspace", tab.WorkspaceId);
                    if (tab is not null && workspace is not null) writer.WriteBoolean("here", tab.WorkspaceId == workspace);
                    if (usage is { } reading)
                    {
                        writer.WriteString("used", TerminalMemoryGuard.Size(reading.Bytes));
                        writer.WriteString("limit", TerminalMemoryGuard.Size(reading.Limit));
                        writer.WriteString("state", reading.State.ToString());
                        if (reading.Largest.Length > 0) writer.WriteString("largest", reading.Largest);
                    }
                    writer.WriteEndObject();
                }
            writer.WriteEndArray();
            writer.WriteStartArray("events");
            foreach (var item in events.Count <= MaxEvents ? events : events.Skip(events.Count - MaxEvents))
            {
                writer.WriteStartObject();
                writer.WriteString("at", item.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                writer.WriteString("kind", item.Kind);
                writer.WriteString("terminal", item.Terminal);
                if (item.Workspace is not null) writer.WriteString("workspace", item.Workspace);
                writer.WriteString("text", item.Text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("eventLog", GuardEventLog.Path);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}