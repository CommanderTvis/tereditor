using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

public sealed record ProvidersAck(bool Ok = true);

/// <summary>One provider in the merged list (bundled, contributed and custom) and whether an agent can use it now.</summary>
/// <param name="Provider">The definition.</param>
/// <param name="Ready">A key is stored, or the provider needs none.</param>
public sealed record ModelProviderRow(ProviderDefinition Provider, bool Ready)
{
    /// <summary>Whether the provider's own sign-in is active, once the host has read it (OpenAI through Codex's login). It never makes a provider ready for a launch; only the list shows it.</summary>
    public ProviderSignIn? SignIn { get; init; }
}

/// <summary>The status of a provider's own sign-in. Only the status: the host never reads or reports a token.</summary>
/// <param name="SignedIn">Whether the sign-in is active.</param>
/// <param name="Method">A short plain name of how, such as "ChatGPT" or "API key"; null when not signed in or unknown.</param>
public sealed record ProviderSignIn(bool SignedIn, string? Method = null);

/// <summary>The agent whose model comes from <paramref name="ProviderId"/>. An agent with no row uses its own provider.</summary>
public sealed record ModelSourceChoice(string AgentId, string ProviderId)
{
    /// <summary>The one model to list for a provider the agent does not know by id and that names no models; null uses the provider's first model, else <c>default</c>.</summary>
    public string? Model { get; init; }
}

/// <summary>The merged provider list and the model source each agent has. It never carries a key.</summary>
public sealed record ModelProvidersReport(IReadOnlyList<ModelProviderRow> Providers, IReadOnlyList<ModelSourceChoice> Sources)
{
    /// <summary>Where the <c>provider-key</c> helper lives on the host: the program an agent runs to read a key. A path, never a key.</summary>
    public string? Helper { get; init; }
}

public sealed record ModelProvidersParams;

public sealed record ModelProviderParams(string Id);

/// <summary>Stores <paramref name="Key"/> for a provider in the key store. It is never returned.</summary>
public sealed record SaveModelKeyParams(string Id, string Key);

/// <summary>The base URL of one API of a provider.</summary>
public sealed record ProviderApiUrl(string Kind, string BaseUrl);

/// <summary>
/// Adds a provider the user declares: a name, the API kinds it speaks, a base URL and optionally a key. <see cref="BaseUrl"/> is the address of every
/// kind that has no entry in <see cref="Urls"/>; a provider with one address for all its APIs sends only that.
/// </summary>
public sealed record AddCustomProviderParams(string Name, string BaseUrl, IReadOnlyList<string> Kinds)
{
    public string? Key { get; init; }

    /// <summary>The address of an API that differs from <see cref="BaseUrl"/>, such as <c>https://api.deepseek.com/anthropic</c> for Anthropic Messages.</summary>
    public IReadOnlyList<ProviderApiUrl>? Urls { get; init; }
}

/// <summary>Changes the base URL of one API of a custom provider.</summary>
public sealed record SetCustomApiUrlParams(string Id, string Kind, string BaseUrl);

/// <summary>Chooses where an agent's model comes from. A null <paramref name="ProviderId"/> means the agent's own provider.</summary>
/// <param name="AgentId">The agent plugin's id, such as <c>claude-code</c>.</param>
/// <param name="ProviderId">A ready provider, or null.</param>
public sealed record SetModelSourceParams(string AgentId, string? ProviderId)
{
    public string? Model { get; init; }
}

/// <summary>Providers a plugin that depends on Model providers adds. They stay until <see cref="WithdrawParams"/> or until Model providers stops.</summary>
/// <param name="Contributor">The contributing plugin's id.</param>
/// <param name="Providers">The definitions.</param>
public sealed record ProviderContribution(string Contributor, IReadOnlyList<ProviderDefinition> Providers);

public sealed record WithdrawParams(string Contributor);

public static class ProvidersContract
{
    public const int WireVersion = 5;

    public static readonly PluginMethod<ModelProvidersParams, ModelProvidersReport> Models = new("models");
    public static readonly PluginMethod<SaveModelKeyParams, ModelProvidersReport> SaveModelKey = new("saveModelKey");
    public static readonly PluginMethod<ModelProviderParams, ModelProvidersReport> RemoveModelKey = new("removeModelKey");
    public static readonly PluginMethod<AddCustomProviderParams, ModelProvidersReport> AddCustomProvider = new("addCustomProvider");
    public static readonly PluginMethod<ModelProviderParams, ModelProvidersReport> RemoveCustomProvider = new("removeCustomProvider");
    public static readonly PluginMethod<SetCustomApiUrlParams, ModelProvidersReport> SetCustomApiUrl = new("setCustomApiUrl");
    public static readonly PluginMethod<SetModelSourceParams, ModelProvidersReport> SetModelSource = new("setModelSource");
    /// <summary>Reads the provider's own sign-in status again (for OpenAI: <c>codex login status</c> on the host) and returns the report with it.</summary>
    public static readonly PluginMethod<ModelProviderParams, ModelProvidersReport> RefreshSignIn = new("refreshSignIn");
    public static readonly PluginMethod<ProviderContribution, ProvidersAck> Contribute = new("contribute");
    public static readonly PluginMethod<WithdrawParams, ProvidersAck> Withdraw = new("withdraw");

    /// <summary>The merged list and the model sources, pushed after every change. The launch contribution and dependent plugins read it.</summary>
    public static readonly PluginChannel<ModelProvidersReport> ModelsChanged = PluginChannel<ModelProvidersReport>.State("models", Models);

    public static readonly PluginContract Contract = PluginContract.Create<ProvidersSettings>(ProvidersManifest.Id, WireVersion,
        [Models, SaveModelKey, RemoveModelKey, AddCustomProvider, RemoveCustomProvider, SetCustomApiUrl, SetModelSource, RefreshSignIn, Contribute, Withdraw], [ModelsChanged], ProvidersJson.Default);
}

public sealed record ProvidersSettings;

[JsonSourceGenerationOptions(Converters = [typeof(PluginJson.EnumConverter<ProviderGroup>), typeof(PluginJson.EnumConverter<ModelAuthKind>)])]
[JsonSerializable(typeof(ProvidersSettings))]
[JsonSerializable(typeof(ProvidersAck))]
[JsonSerializable(typeof(ModelProvidersParams))]
[JsonSerializable(typeof(ModelProvidersReport))]
[JsonSerializable(typeof(ModelProviderParams))]
[JsonSerializable(typeof(SaveModelKeyParams))]
[JsonSerializable(typeof(AddCustomProviderParams))]
[JsonSerializable(typeof(SetCustomApiUrlParams))]
[JsonSerializable(typeof(SetModelSourceParams))]
[JsonSerializable(typeof(ProviderContribution))]
[JsonSerializable(typeof(WithdrawParams))]
internal sealed partial class ProvidersJson : JsonSerializerContext;