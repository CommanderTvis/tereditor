namespace Tereditor.Plugins.Providers;

/// <summary>What the providers share: the note on Anthropic and the rule for an id.</summary>
public static class ProviderCatalog
{
    public const string AnthropicNote = "Use an API key from the Claude Console. A Claude subscription sign-in is not supported here.";

    /// <summary>A provider id is lower case letters, digits, dots, dashes and underscores.</summary>
    public static bool IsProviderId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.');
}
