using System.Text.RegularExpressions;

using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Chat.UI;

/// <summary>
/// The images of one draft. A pasted, dropped or picked image goes into the box as one token, <c>[Image #N]</c>, and its bytes are kept here;
/// the token is the only handle: an image is sent exactly when its token is in the text, and deleting the token drops the image from the message.
/// Items stay until the send, so Undo that brings a token back brings its image back too. Only a token that names a kept image counts; any other
/// <c>[Image #N]</c> is plain text.
/// </summary>
internal sealed partial class PastedImages
{
    [GeneratedRegex(@"\[Image #(?<n>\d+)\]")]
    private static partial Regex Pattern();

    private readonly Dictionary<int, ChatAttachment> items = [];
    private int last;

    internal static string Token(int number) => $"[Image #{number}]";

    internal static string Name(int number) => $"Image #{number}";

    /// <summary>
    /// Keeps <paramref name="image"/> and returns its number. Numbers start at 1 per draft and go past every <c>[Image #N]</c> already in
    /// <paramref name="text"/> (one recalled from history is plain text), so a new token never reads as an old one.
    /// </summary>
    internal int Add(ChatAttachment image, string text)
    {
        foreach (Match match in Pattern().Matches(text))
            if (int.TryParse(match.Groups["n"].ValueSpan, out var seen)) last = Math.Max(last, seen);
        items[++last] = image with { Name = Name(last) };
        return last;
    }

    internal ChatAttachment? Item(int number) => items.GetValueOrDefault(number);

    internal void Set(int number, ChatAttachment image) { if (items.ContainsKey(number)) items[number] = image; }

    internal void Remove(int number) => items.Remove(number);

    /// <summary>The live tokens in <paramref name="text"/>, in order: [Start, End) and the image each stands for.</summary>
    internal IEnumerable<(int Start, int End, int Number)> Tokens(string text)
    {
        if (items.Count == 0) yield break;
        foreach (Match match in Pattern().Matches(text))
            if (int.TryParse(match.Groups["n"].ValueSpan, out var number) && items.ContainsKey(number)) yield return (match.Index, match.Index + match.Length, number);
    }


    /// <summary>The images whose token is in <paramref name="text"/>, each once, in the order of their first token: what a send carries.</summary>
    internal IReadOnlyList<(int Number, ChatAttachment Item)> Live(string text) =>
        [.. Tokens(text).Select(token => token.Number).Distinct().Select(number => (number, items[number]))];

    /// <summary>The images a recalled message keeps, each under its own number: its tokens in the box resolve to them while it is recalled.</summary>
    internal static PastedImages Recalled(IEnumerable<(int Number, ChatAttachment Item)> kept)
    {
        var images = new PastedImages();
        foreach (var (number, item) in kept) images.items[number] = item;
        return images;
    }

    /// <summary>The numbers of the <c>[Image #N]</c> tokens in <paramref name="text"/> with no image here: they are plain text.</summary>
    internal IReadOnlyList<int> Unkept(string text)
    {
        var missing = new List<int>();
        foreach (Match match in Pattern().Matches(text))
            if (int.TryParse(match.Groups["n"].ValueSpan, out var number) && !items.ContainsKey(number) && !missing.Contains(number)) missing.Add(number);
        return missing;
    }
}