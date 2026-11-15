using System.Text.Json.Serialization;

namespace Tereditor.Plugins.Api;

/// <summary>
/// The model API kinds an agent can accept and a model provider can offer. A kind names the structure of the API: an agent
/// that takes <see cref="AnthropicMessages"/> cannot use a provider that only offers <see cref="OpenAiChat"/>. tereditor
/// never translates between kinds.
/// </summary>
public static class ModelApiKinds
{
    /// <summary>Anthropic's Messages API.</summary>
    public const string AnthropicMessages = "anthropic-messages";

    /// <summary>OpenAI's Responses API.</summary>
    public const string OpenAiResponses = "openai-responses";

    /// <summary>OpenAI's Chat Completions API, which many routers and providers offer.</summary>
    public const string OpenAiChat = "openai-chat";

    /// <summary>Google's Gemini API.</summary>
    public const string GoogleGenerative = "google-generative";

    /// <summary>Google Cloud Vertex AI.</summary>
    public const string Vertex = "vertex";

    /// <summary>Amazon Bedrock.</summary>
    public const string Bedrock = "bedrock";

    /// <summary>Every known kind.</summary>
    public static IReadOnlyList<string> All { get; } = [AnthropicMessages, OpenAiResponses, OpenAiChat, GoogleGenerative, Vertex, Bedrock];

    /// <summary>Whether <paramref name="kind"/> is one of <see cref="All"/>.</summary>
    /// <param name="kind">The kind.</param>
    public static bool IsKnown(string kind) => All.Contains(kind);
}

/// <summary>How a credential reaches the API: an API key, or an OAuth sign-in.</summary>
public enum ModelAuthKind
{
    /// <summary>A secret key sent with each request.</summary>
    [JsonStringEnumMemberName("api-key")] ApiKey,

    /// <summary>A sign-in that yields a token.</summary>
    [JsonStringEnumMemberName("oauth")] OAuth
}

/// <summary>How tereditor hands a provider to an agent: the mechanism the agent itself reads.</summary>
public enum AgentInjection
{
    /// <summary>A settings file passed on the launch line (Claude Code <c>--settings</c>).</summary>
    [JsonStringEnumMemberName("settings-file")] SettingsFile,

    /// <summary>Configuration overrides passed on the launch line (Codex <c>-c</c>).</summary>
    [JsonStringEnumMemberName("config-override")] ConfigOverride,

    /// <summary>A models file the agent reads (Pi <c>models.json</c>, omp <c>models.yml</c>).</summary>
    [JsonStringEnumMemberName("models-file")] ModelsFile
}

/// <summary>One API kind an agent accepts.</summary>
/// <param name="Kind">A <see cref="ModelApiKinds"/> value.</param>
/// <param name="Injection">How a provider reaches the agent.</param>
/// <param name="Auth">The credential kinds the agent accepts for this API.</param>
public sealed record AgentModelApi(string Kind, AgentInjection Injection, IReadOnlyList<ModelAuthKind> Auth);

/// <summary>
/// What an agent plugin accepts for its model, declared in its manifest. It is data that travels with the plugin roster, so
/// it exists with no consumer: an agent never depends on a plugin that fills it in, and with none present the agent
/// handles its own model as it always did.
/// </summary>
/// <param name="Accepts">The API kinds the agent accepts, with how each is injected.</param>
public sealed record AgentRouting(IReadOnlyList<AgentModelApi> Accepts)
{
    /// <summary>Whether the agent has its own sign-in or key handling, so using its own provider needs nothing from tereditor.</summary>
    public bool OwnLogin { get; init; } = true;
}
