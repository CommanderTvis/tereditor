using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

/// <summary>The block of Settings › Plugins › Model providers a provider is listed in.</summary>
public enum ProviderGroup
{
    /// <summary>JetBrains AI. Filled in by the Air Gateway plugin.</summary>
    [JsonStringEnumMemberName("jetbrains-ai")] JetBrainsAi,

    /// <summary>Cloud platforms: Vertex AI, Bedrock, Azure, Foundry. Each is its own plugin.</summary>
    [JsonStringEnumMemberName("hyperscalers")] Hyperscalers,

    /// <summary>Routers and providers that need only an endpoint and a key.</summary>
    [JsonStringEnumMemberName("other")] Other
}

/// <summary>One API a provider offers.</summary>
/// <param name="Kind">A <see cref="ModelApiKinds"/> value.</param>
/// <param name="BaseUrl">The API's base URL.</param>
/// <param name="Auth">How a credential reaches this API. Sign-in is offered only when the provider's OAuth policy allows it.</param>
public sealed record ProviderApi(string Kind, string BaseUrl, IReadOnlyList<ModelAuthKind> Auth);

/// <summary>
/// Whether a provider may be used through a sign-in instead of a key. A provider with no policy, or a vendor whose
/// permission is not known, is never offered a sign-in. No sign-in code lives in the Providers plugin.
/// </summary>
/// <param name="Allowed">Whether the vendor lets other apps use its sign-in.</param>
/// <param name="Flow">The flow that would be used, such as <c>authorization-code-pkce</c>; null when none is.</param>
/// <param name="Vendor">The vendor the policy is about.</param>
/// <param name="Note">A plain sentence the page shows next to an allowed sign-in.</param>
/// <param name="ReasonIfNot">A plain sentence saying why the sign-in is not offered; null when it is.</param>
public sealed record ProviderOAuth(bool Allowed, string? Flow = null, string? Vendor = null, string? Note = null, string? ReasonIfNot = null)
{
    /// <summary>Whether the user may mark the sign-in as allowed. False when the vendor forbids it.</summary>
    public bool Overridable { get; init; }
}

/// <summary>The sign-in policy per vendor.</summary>
public static class ProviderOAuthPolicy
{
    /// <summary>OpenAI's "Sign in with ChatGPT" for open-source and locally hosted apps.</summary>
    public const string OpenAi = "openai";

    /// <summary>Anthropic: a Claude subscription sign-in is not allowed in other apps.</summary>
    public const string Anthropic = "anthropic";

    /// <summary>Google: the Gemini CLI sign-in is not allowed in other apps.</summary>
    public const string Google = "google";

    /// <summary>The policy for <paramref name="vendor"/>. A vendor the table does not name is not offered, and the user may mark it.</summary>
    /// <param name="vendor">The vendor id.</param>
    public static ProviderOAuth For(string vendor) => For(vendor, OperatingSystem.IsBrowser());

    /// <summary>The policy for <paramref name="vendor"/> on a host that is or is not a browser tab. A tab has no loopback listener and no sign-in helper, so no sign-in is offered there.</summary>
    /// <param name="vendor">The vendor id.</param>
    /// <param name="browser">Whether the host is the Playground.</param>
    public static ProviderOAuth For(string vendor, bool browser) => browser && vendor == OpenAi
        ? new(false, null, OpenAi, null, "Sign in with ChatGPT needs a local helper, so it is not available in the browser. Use an API key.") { Overridable = false }
        : Vendor(vendor);

    private static ProviderOAuth Vendor(string vendor) => vendor switch
    {
        OpenAi => new(true, "authorization-code-pkce", OpenAi, "Sign in with ChatGPT. A remote host needs OpenAI's approval for hosted apps.", null),
        Anthropic => new(false, null, Anthropic, null, "Anthropic does not allow a Claude subscription sign-in in other apps. Use an API key.") { Overridable = false },
        Google => new(false, null, Google, null, "Google does not allow the Gemini CLI sign-in in other apps. Use an API key.") { Overridable = false },
        _ => new(false, null, vendor, null, "No permission for this sign-in is known, so it is not offered.") { Overridable = true }
    };
}

/// <summary>A model provider: the APIs it offers and how a credential reaches them.</summary>
/// <param name="Id">The provider's id, also the name of its key in the key store.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Group">Where the page lists it.</param>
/// <param name="Apis">The APIs it offers.</param>
public sealed record ProviderDefinition(string Id, string Name, ProviderGroup Group, IReadOnlyList<ProviderApi> Apis)
{
    /// <summary>Whether a key must be stored before an agent can use the provider. False for a keyless local endpoint or a provider that signs in itself.</summary>
    public bool NeedsKey { get; set; } = true;

    /// <summary>A plain sentence the page shows with the provider.</summary>
    public string? Note { get; init; }

    /// <summary>The sign-in policy; null when the provider takes keys only.</summary>
    public ProviderOAuth? OAuth { get; init; }

    /// <summary>Model ids the provider serves, written into an agent's models file when the agent does not know the provider by id. Empty when unknown.</summary>
    public IReadOnlyList<string> Models { get; set; } = [];

    /// <summary>
    /// Non-secret variables a launch of an agent that runs on this platform sets (region, profile, project). Only a provider whose API takes the
    /// platform's own credentials has any; the credentials themselves stay with the platform's tools and never pass through tereditor.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();

    /// <summary>Added by the user.</summary>
    public bool Custom { get; init; }

    /// <summary>The plugin that contributed the provider; null for a bundled or custom one.</summary>
    public string? Contributor { get; init; }
}

/// <summary>One way an agent can use a provider: an API both declare, and the credential kind that fits.</summary>
/// <param name="Agent">The agent's side: kind, injection and accepted credentials.</param>
/// <param name="Provider">The provider's side.</param>
/// <param name="Auth">The credential kind used. A key is preferred.</param>
public sealed record ProviderOffer(AgentModelApi Agent, ProviderApi Provider, ModelAuthKind Auth);

/// <summary>Decides which providers an agent is offered. Pure; the agent's side comes from the roster, the provider's from its definition.</summary>
public static class ProviderMatching
{
    /// <summary>
    /// The ways <paramref name="routing"/>'s agent can use <paramref name="provider"/>: for each API kind both declare, the
    /// credential kinds both accept. A sign-in counts only when the provider's policy allows it. No API in common, or no
    /// credential kind in common, means no offer.
    /// </summary>
    /// <param name="routing">What the agent accepts.</param>
    /// <param name="provider">The provider.</param>
    public static IReadOnlyList<ProviderOffer> Offers(AgentRouting routing, ProviderDefinition provider)
    {
        var offers = new List<ProviderOffer>();
        foreach (var accepted in routing.Accepts)
            foreach (var api in provider.Apis.Where(candidate => candidate.Kind == accepted.Kind))
            {
                var auth = accepted.Auth.Where(kind => api.Auth.Contains(kind) && (kind is ModelAuthKind.ApiKey or ModelAuthKind.Platform || provider.OAuth is { Allowed: true })).ToArray();
                if (auth.Length > 0) offers.Add(new(accepted, api, auth.Contains(ModelAuthKind.ApiKey) ? ModelAuthKind.ApiKey : auth[0]));
            }
        return offers;
    }

    /// <summary>Whether the agent is offered the provider at all.</summary>
    /// <param name="routing">What the agent accepts.</param>
    /// <param name="provider">The provider.</param>
    public static bool Offered(AgentRouting routing, ProviderDefinition provider) => Offers(routing, provider).Count > 0;
}

/// <summary>The named places of Settings › Plugins › Model providers that another plugin fills with a control, through <c>SlotSection</c>.</summary>
public static class ProvidersSlots
{
    /// <summary>The JetBrains AI block. Empty until the Air Gateway plugin contributes its component.</summary>
    public const string JetBrainsAi = "providers:jetbrains-ai";

    /// <summary>The Cloud platforms block (Vertex AI, Bedrock, Azure, Foundry). Empty until a plugin contributes a component.</summary>
    public const string Hyperscalers = "providers:hyperscalers";

    /// <summary>The block after the bundled routers and providers, for a component that belongs with them.</summary>
    public const string Other = "providers:other";
}

/// <summary>The providers that need only an endpoint and a key, bundled as data. Documented endpoints; none of them carries code.</summary>
public static class BundledProviders
{
    private static readonly IReadOnlyList<ModelAuthKind> Key = [ModelAuthKind.ApiKey];

    private static ProviderApi Api(string kind, string url) => new(kind, url, Key);

    private static ProviderDefinition Other(string id, string name, params ProviderApi[] apis) => new(id, name, ProviderGroup.Other, apis);

    /// <summary>The bundled providers.</summary>
    public static IReadOnlyList<ProviderDefinition> All { get; } =
    [
        Other("anthropic", "Anthropic API", Api(ModelApiKinds.AnthropicMessages, "https://api.anthropic.com")) with
        {
            Note = ProviderCatalog.AnthropicNote,
            OAuth = ProviderOAuthPolicy.For(ProviderOAuthPolicy.Anthropic)
        },
        Other("openai", "OpenAI API", Api(ModelApiKinds.OpenAiResponses, "https://api.openai.com/v1"), Api(ModelApiKinds.OpenAiChat, "https://api.openai.com/v1")),
        Other("openrouter", "OpenRouter", Api(ModelApiKinds.OpenAiChat, "https://openrouter.ai/api/v1"), Api(ModelApiKinds.OpenAiResponses, "https://openrouter.ai/api/v1"),
            Api(ModelApiKinds.AnthropicMessages, "https://openrouter.ai/api")),
        Other("deepseek", "DeepSeek", Api(ModelApiKinds.OpenAiChat, "https://api.deepseek.com"), Api(ModelApiKinds.AnthropicMessages, "https://api.deepseek.com/anthropic")),
        Other("mistral", "Mistral", Api(ModelApiKinds.OpenAiChat, "https://api.mistral.ai/v1")),
        Other("groq", "Groq", Api(ModelApiKinds.OpenAiChat, "https://api.groq.com/openai/v1"), Api(ModelApiKinds.OpenAiResponses, "https://api.groq.com/openai/v1")),
        Other("together", "Together", Api(ModelApiKinds.OpenAiChat, "https://api.together.xyz/v1")),
        Other("fireworks", "Fireworks", Api(ModelApiKinds.OpenAiChat, "https://api.fireworks.ai/inference/v1")),
        Other("xai", "xAI", Api(ModelApiKinds.OpenAiChat, "https://api.x.ai/v1"), Api(ModelApiKinds.OpenAiResponses, "https://api.x.ai/v1"),
            Api(ModelApiKinds.AnthropicMessages, "https://api.x.ai")),
        Other("cerebras", "Cerebras", Api(ModelApiKinds.OpenAiChat, "https://api.cerebras.ai/v1"))
    ];
}