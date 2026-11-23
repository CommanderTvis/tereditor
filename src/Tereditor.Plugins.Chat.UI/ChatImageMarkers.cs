using System.Text.RegularExpressions;

namespace Tereditor.Plugins.Chat.UI;

/// <summary>
/// The marker line the Claude CLI puts into a prompt for an attached image: <c>[Image: source: /tmp/…/3.png]</c> (the model reads the file)
/// or <c>[Image #3]</c> (a reference to an image that came with the prompt). The bubble shows each as a name, never as the raw text;
/// the prompt's own text is not changed.
/// </summary>
internal static partial class ChatImageMarkers
{
    [GeneratedRegex(@"[ \t]*\[Image(?: #(?<n>\d+))?(?:: source: (?<path>[^\]\r\n]+))?\][ \t]*")]
    private static partial Regex Marker();

    /// <summary>The text without its markers, and one (name, path) per marker; the path is empty for an <c>[Image #N]</c> reference.</summary>
    internal static (string Text, IReadOnlyList<(string Name, string Path)> Images) Split(string text, IReadOnlyCollection<string>? attached = null, HashSet<string>? kept = null)
    {
        if (text.IndexOf("[Image", StringComparison.Ordinal) < 0) return (text, []);
        var images = new List<(string, string)>();
        // Fenced code is left as written: only the lines outside a fence are searched.
        var lines = text.Split('\n');
        var fence = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)) { fence = !fence; continue; }
            if (fence) continue;
            var before = images.Count;
            var stripped = Strip(lines[index], images, attached, kept);
            if (images.Count > before) lines[index] = stripped.Trim(' ', '\t');
        }
        if (images.Count == 0) return (text, []);
        return (string.Join('\n', lines).Trim(), images);
    }

    /// <summary>
    /// The text and chips of a user bubble. An <c>[Image #N]</c> whose image came with the message as the attachment "Image #N" is the composer's own
    /// pointer: it stays in the text and its attachment draws no chip of its own.
    /// </summary>
    internal static (string Text, List<(string Name, string Path)> Chips) Chips(string text, IReadOnlyList<ChatAttachment>? attachments)
    {
        var names = attachments?.Select(item => item.Name).ToHashSet() ?? [];
        var kept = new HashSet<string>();
        var (shown, images) = Split(text, names, kept);
        return (shown, [.. (attachments ?? []).Where(item => !kept.Contains(item.Name)).Select(item => (item.Name, Path: "")).Concat(images)]);
    }

    private static string Strip(string line, List<(string, string)> images, IReadOnlyCollection<string>? attached, HashSet<string>? kept) =>
        Marker().Replace(line, match =>
        {
            var path = match.Groups["path"].Success ? match.Groups["path"].Value.Trim() : "";
            if (path.Length == 0 && !match.Groups["n"].Success) return match.Value;
            if (path.Length == 0 && attached is not null && "Image #" + match.Groups["n"].Value is var own && attached.Contains(own)) { kept?.Add(own); return match.Value; }
            var name = path.Length > 0 ? path[(path.LastIndexOfAny(['/', '\\']) + 1)..] : "Image #" + match.Groups["n"].Value;
            images.Add((name.Length > 0 ? name : path, path));
            return " ";
        });
}