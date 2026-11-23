using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

/// <summary>What the providers share: the note on Anthropic and the rule for an id.</summary>
public static class ProviderCatalog
{
    public const string AnthropicNote = "Use an API key from the Claude Console. A Claude subscription sign-in is not supported here.";

    /// <summary>Where a Claude Max or Team subscriber gets API credit; shown under the Anthropic key and in the Playground guide.</summary>
    public const string AnthropicCredits = "Claude Max and Team plans include monthly API credits. Claim them in your Claude account, then create a key in the Claude Console.";

    public const string AnthropicCreditsUrl = "https://platform.claude.com/docs/en/about-claude/api-credits-for-subscribers";

    /// <summary>Where a Google AI Pro or Ultra subscriber gets Cloud credit for the Gemini API; shown under the Google key and in the Playground guide.</summary>
    public const string GoogleCredits = "Google AI Pro and Ultra include Cloud credits for the Gemini API. Activate them, then create a Gemini API key.";

    public const string GoogleCreditsUrl = "https://developers.google.com/program";

    public const string OmpAgent = "omp";

    /// <summary>The id of JetBrains (Air Gateway), which the Air Gateway plugin contributes.</summary>
    public const string JetBrainsAi = "jetbrains-ai";

    /// <summary>The omp profile tereditor owns; omp's launch line asks for it with <c>--profile</c>.</summary>
    public static AgentLaunchContribution OmpLaunch { get; } = new() { Arguments = ["--profile", "tereditor"] };

    /// <summary>A provider id is lower case letters, digits, dots, dashes and underscores.</summary>
    public static bool IsProviderId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.');
}