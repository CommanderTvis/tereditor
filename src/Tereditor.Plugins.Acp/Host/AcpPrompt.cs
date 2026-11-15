using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>What one <c>session/prompt</c> carries, and what the chat should tell the user about what was left out or changed.</summary>
internal sealed record AcpPrompt(JsonArray Content, IReadOnlyList<(ChatNoticeLevel Level, string Text)> Notices);

/// <summary>
/// Turns the text and the attachments of a message into ACP content blocks. All paths are host paths. A selection is embedded text when the
/// agent takes embedded context, else a link plus the text in a fenced block; an image goes inline only when the agent takes images.
/// The editor's context (kind "selection" or "file") is added only for files inside the workspace that the project does not ignore.
/// </summary>
internal static class AcpPromptBuilder
{
    /// <summary>The most selected text that is embedded; a larger selection is linked with its range instead.</summary>
    public const int MaxEmbeddedChars = 100_000;
    private const long MaxReadBytes = 8 * 1024 * 1024;

    public static async Task<AcpPrompt> BuildAsync(string text, IReadOnlyList<ChatAttachment>? attachments, AcpAgentInfo? agent, string cwd, CancellationToken cancellationToken = default)
    {
        var blocks = new List<JsonNode>();
        var notices = new List<(ChatNoticeLevel, string)>();
        var linked = new HashSet<string>(StringComparer.Ordinal);
        var root = AcpClientMethods.Resolve(Path.GetFullPath(cwd));
        var embedded = agent is { EmbeddedContext: true };

        foreach (var attachment in attachments ?? [])
        {
            switch (attachment.Kind)
            {
                case "selection":
                case "file":
                    await EditorContextAsync(attachment, root, cwd, embedded, blocks, notices, linked, cancellationToken);
                    break;
                case "image" or "document" when attachment.Data is { Length: > 0 } data:
                    Inline(attachment, data, agent, cwd, blocks, notices, linked);
                    break;
                default:
                    if (attachment.Path.Length > 0) Link(blocks, linked, attachment.Name, Full(attachment.Path, cwd), null);
                    break;
            }
        }

        // A finished @path in the text is a file reference; only a file inside the workspace becomes one (a path elsewhere stays plain text).
        foreach (var (path, range, _, _) in ChatMentions.Parse(text + " "))
        {
            var full = AcpClientMethods.Resolve(Full(path, cwd));
            if (!Inside(root, full) || !(File.Exists(full) || Directory.Exists(full))) continue;
            if (range.Length > 0 && File.Exists(full) && ParseRange(range) is var (start, end))
                await SelectionAsync(full, root, start, end, null, embedded, blocks, notices, linked, cancellationToken);
            else Link(blocks, linked, Path.GetFileName(full), full, null);
        }

        var content = new JsonArray();
        if (text.Length > 0 || blocks.Count == 0) content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var block in blocks) content.Add(block);
        return new AcpPrompt(content, notices);
    }

    private static string Full(string path, string cwd) => Path.GetFullPath(path, cwd);

    internal static bool Inside(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static (int Start, int End)? ParseRange(string range)
    {
        var match = System.Text.RegularExpressions.Regex.Match(range, @"#L(\d+)(?:-(\d+))?");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var start) || start < 1) return null;
        return (start, match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var end) && end >= start ? end : start);
    }

    private static string Display(string root, string full) => Inside(root, full) ? Path.GetRelativePath(root, full).Replace('\\', '/') : full;

    private static string Uri(string full) => new Uri(Directory.Exists(full) && !full.EndsWith(Path.DirectorySeparatorChar) ? full + Path.DirectorySeparatorChar : full).AbsoluteUri;

    private static string? MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".md" => "text/markdown",
        ".html" or ".htm" => "text/html",
        ".json" => "application/json",
        ".txt" => "text/plain",
        _ => null
    };

    /// <summary>A resource_link to a host path; a folder keeps a trailing slash in its name and uri. A file's size rides along.</summary>
    private static void Link(List<JsonNode> blocks, HashSet<string> linked, string name, string full, string? label)
    {
        var uri = Uri(full);
        if (!linked.Add(uri)) return;
        var link = new JsonObject { ["type"] = "resource_link", ["name"] = label ?? (Directory.Exists(full) ? name + "/" : name), ["uri"] = uri };
        if (File.Exists(full))
        {
            if (MimeOf(full) is { } mime) link["mimeType"] = mime;
            try { link["size"] = new FileInfo(full).Length; } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        blocks.Add(link);
    }

    private static void Inline(ChatAttachment attachment, byte[] data, AcpAgentInfo? agent, string cwd, List<JsonNode> blocks, List<(ChatNoticeLevel, string)> notices, HashSet<string> linked)
    {
        var mime = attachment.MediaType ?? MimeOf(attachment.Name) ?? "application/octet-stream";
        var image = attachment.Kind == "image";
        if (image && agent is { Image: true })
        {
            blocks.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mime });
            return;
        }
        if (!image && agent is { EmbeddedContext: true } && attachment.Path.Length == 0)
        {
            blocks.Add(new JsonObject { ["type"] = "resource", ["resource"] = new JsonObject { ["uri"] = "attachment:///" + System.Uri.EscapeDataString(attachment.Name), ["mimeType"] = mime, ["blob"] = Convert.ToBase64String(data) } });
            return;
        }
        // The agent cannot take the bytes: a file on disk is linked for it to read; bytes with no file (a paste) cannot go at all, and the user is told.
        if (attachment.Path.Length > 0)
        {
            Link(blocks, linked, attachment.Name, Full(attachment.Path, cwd), null);
            notices.Add((ChatNoticeLevel.Warning, $"This agent does not take {(image ? "images" : "documents")} in a message, so {attachment.Name} went as a link to the file."));
        }
        else notices.Add((ChatNoticeLevel.Warning, $"{attachment.Name} was not sent: this agent does not take {(image ? "images" : "documents")} in a message."));
    }

    private static async Task EditorContextAsync(ChatAttachment attachment, string root, string cwd, bool embedded, List<JsonNode> blocks, List<(ChatNoticeLevel, string)> notices, HashSet<string> linked, CancellationToken cancellationToken)
    {
        if (attachment.Path.Length == 0) return;
        var full = AcpClientMethods.Resolve(Full(attachment.Path, cwd));
        var name = Path.GetFileName(full);
        // The editor's context was not picked by the user, so it stays inside the workspace and out of what the project ignores.
        if (!Inside(root, full)) { notices.Add((ChatNoticeLevel.Info, $"Left {name} out: it is outside the workspace.")); return; }
        if (await IgnoredAsync(root, full, cancellationToken)) { notices.Add((ChatNoticeLevel.Info, $"Left {name} out: the project ignores it.")); return; }
        if (attachment.Kind == "file") { Link(blocks, linked, name, full, null); return; }
        await SelectionAsync(full, root, attachment.StartLine ?? 1, attachment.EndLine ?? attachment.StartLine ?? 1, attachment.Quote, embedded, blocks, notices, linked, cancellationToken);
    }

    private static async Task SelectionAsync(string full, string root, int start, int end, string? quote, bool embedded, List<JsonNode> blocks, List<(ChatNoticeLevel, string)> notices, HashSet<string> linked, CancellationToken cancellationToken)
    {
        var shown = Display(root, full);
        var label = quote is null ? $"{shown}:{start}-{end}" : $"{shown} (selected text)";
        var uri = Uri(full);
        string? body = quote;
        if (body is null)
        {
            try
            {
                if (new FileInfo(full).Length > MaxReadBytes) throw new IOException("The file is too large.");
                var lines = await File.ReadAllLinesAsync(full, cancellationToken);
                body = string.Join('\n', lines.Skip(start - 1).Take(end - start + 1));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Link(blocks, linked, Path.GetFileName(full), full, label);
                notices.Add((ChatNoticeLevel.Warning, $"Could not read {label} ({error.Message}); the agent got a link to the file."));
                return;
            }
        }
        if (body.Length > MaxEmbeddedChars)
        {
            Link(blocks, linked, Path.GetFileName(full), full, label);
            notices.Add((ChatNoticeLevel.Info, $"{label} is too large to include, so the agent got a link with the range."));
            return;
        }
        var where = new JsonObject { ["kind"] = "selection", ["path"] = full };
        if (quote is null) { where["startLine"] = start; where["endLine"] = end; }
        var meta = new JsonObject { ["tereditor"] = where };
        if (embedded)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "resource",
                ["resource"] = new JsonObject { ["uri"] = uri, ["mimeType"] = "text/plain", ["text"] = label + "\n" + body },
                ["_meta"] = meta
            });
            return;
        }
        Link(blocks, linked, Path.GetFileName(full), full, label);
        var longest = 0;
        for (var at = 0; at < body.Length; at++)
        {
            var run = 0;
            while (at + run < body.Length && body[at + run] == '`') run++;
            longest = Math.Max(longest, run);
            at += run;
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = new StringBuilder(label).Append('\n').Append(fence).Append('\n').Append(body).Append('\n').Append(fence).ToString() });
    }

    /// <summary>Whether Git says the project ignores the path; no Git, no repository or a slow answer is "not ignored".</summary>
    internal static async Task<bool> IgnoredAsync(string root, string full, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            foreach (var argument in new[] { "check-ignore", "-q", "--", full }) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } return false; }
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }
}