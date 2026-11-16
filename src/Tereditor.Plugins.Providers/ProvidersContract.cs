using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

/// <summary>The agents whose model providers are managed here. Each reads its own credential store.</summary>
public enum ProviderAgent
{
    [JsonStringEnumMemberName("pi")] Pi,
    [JsonStringEnumMemberName("omp")] Omp
}

/// <summary>Where a provider's credential comes from, as far as the host can tell without reading the secret.</summary>
public enum ProviderSource
{
    /// <summary>No credential is stored by the agent's own files.</summary>
    [JsonStringEnumMemberName("none")] None,
    /// <summary>An API key tereditor or the agent stored in the agent's file.</summary>
    [JsonStringEnumMemberName("key")] Key,
    /// <summary>A sign-in managed by the agent itself (an OAuth login); not editable here.</summary>
    [JsonStringEnumMemberName("sign-in")] SignIn,
    /// <summary>The provider's environment variable is set in the host's environment.</summary>
    [JsonStringEnumMemberName("environment")] Environment
}

/// <summary>One provider row. Nothing here carries a key.</summary>
/// <param name="Id">The id the agent uses for the provider (<c>anthropic</c>).</param>
/// <param name="EnvVar">The variable the agent reads the key from; omp stores a key as this variable.</param>
public sealed record ProviderEntry(string Id, string Name, string? EnvVar, ProviderSource Source)
{
    /// <summary>Added by the user rather than listed in the agent's documentation.</summary>
    public bool Custom { get; init; }
    /// <summary>Why a key cannot be entered for this provider, such as a subscription sign-in; null when one can.</summary>
    public string? Note { get; init; }
    public bool Configured => Source != ProviderSource.None;
}

/// <summary>An agent's providers and the file their keys live in.</summary>
public sealed record ProvidersReport(ProviderAgent Agent, string Location, IReadOnlyList<ProviderEntry> Providers)
{
    /// <summary>What the agent needs for these keys to apply, such as a launch with tereditor's profile.</summary>
    public string? Note { get; init; }
}

public sealed record ProvidersParams(ProviderAgent Agent);

/// <summary>Stores <paramref name="Key"/> for a provider. <paramref name="EnvVar"/> names the variable of a provider the catalog does not list.</summary>
public sealed record SetProviderKeyParams(ProviderAgent Agent, string ProviderId, string Key)
{
    public string? Name { get; init; }
    public string? EnvVar { get; init; }
}

public sealed record RemoveProviderKeyParams(ProviderAgent Agent, string ProviderId);

public sealed record ProvidersAck(bool Ok = true);

/// <summary>One provider in the merged list (bundled, contributed and custom) and whether an agent can use it now.</summary>
/// <param name="Provider">The definition.</param>
/// <param name="Ready">A key is stored, or the provider needs none.</param>
public sealed record ModelProviderRow(ProviderDefinition Provider, bool Ready);

/// <summary>The agent whose model comes from <paramref name="ProviderId"/>. An agent with no row uses its own provider.</summary>
public sealed record ModelSourceChoice(string AgentId, string ProviderId);

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

/// <summary>Adds a provider the user declares: a name, one base URL, the API kinds that URL speaks, and optionally a key.</summary>
public sealed record AddCustomProviderParams(string Name, string BaseUrl, IReadOnlyList<string> Kinds)
{
    public string? Key { get; init; }
}

/// <summary>Chooses where an agent's model comes from. A null <paramref name="ProviderId"/> means the agent's own provider.</summary>
/// <param name="AgentId">The agent plugin's id, such as <c>claude-code</c>.</param>
/// <param name="ProviderId">A ready provider, or null.</param>
public sealed record SetModelSourceParams(string AgentId, string? ProviderId);

/// <summary>Providers a plugin that depends on Model providers adds. They stay until <see cref="WithdrawParams"/> or until Model providers stops.</summary>
/// <param name="Contributor">The contributing plugin's id.</param>
/// <param name="Providers">The definitions.</param>
public sealed record ProviderContribution(string Contributor, IReadOnlyList<ProviderDefinition> Providers);

public sealed record WithdrawParams(string Contributor);

public static class ProvidersContract
{
    public const int WireVersion = 3;

    public static readonly PluginMethod<ProvidersParams, ProvidersReport> List = new("list");
    public static readonly PluginMethod<SetProviderKeyParams, ProvidersReport> SetKey = new("setKey");
    public static readonly PluginMethod<RemoveProviderKeyParams, ProvidersReport> RemoveKey = new("removeKey");

    public static readonly PluginMethod<ModelProvidersParams, ModelProvidersReport> Models = new("models");
    public static readonly PluginMethod<SaveModelKeyParams, ModelProvidersReport> SaveModelKey = new("saveModelKey");
    public static readonly PluginMethod<ModelProviderParams, ModelProvidersReport> RemoveModelKey = new("removeModelKey");
    public static readonly PluginMethod<AddCustomProviderParams, ModelProvidersReport> AddCustomProvider = new("addCustomProvider");
    public static readonly PluginMethod<ModelProviderParams, ModelProvidersReport> RemoveCustomProvider = new("removeCustomProvider");
    public static readonly PluginMethod<SetModelSourceParams, ModelProvidersReport> SetModelSource = new("setModelSource");
    public static readonly PluginMethod<ProviderContribution, ProvidersAck> Contribute = new("contribute");
    public static readonly PluginMethod<WithdrawParams, ProvidersAck> Withdraw = new("withdraw");

    /// <summary>The merged list and the model sources, pushed after every change. The launch contribution and dependent plugins read it.</summary>
    public static readonly PluginChannel<ModelProvidersReport> ModelsChanged = PluginChannel<ModelProvidersReport>.State("models", Models);

    public static readonly PluginContract Contract = PluginContract.Create<ProvidersSettings>(ProvidersManifest.Id, WireVersion,
        [List, SetKey, RemoveKey, Models, SaveModelKey, RemoveModelKey, AddCustomProvider, RemoveCustomProvider, SetModelSource, Contribute, Withdraw], [ModelsChanged], ProvidersJson.Default);
}

public sealed record ProvidersSettings;

[JsonSourceGenerationOptions(Converters = [typeof(PluginJson.EnumConverter<ProviderAgent>), typeof(PluginJson.EnumConverter<ProviderSource>),
    typeof(PluginJson.EnumConverter<ProviderGroup>), typeof(PluginJson.EnumConverter<ModelAuthKind>)])]
[JsonSerializable(typeof(ProvidersSettings))]
[JsonSerializable(typeof(ProvidersParams))]
[JsonSerializable(typeof(ProvidersReport))]
[JsonSerializable(typeof(SetProviderKeyParams))]
[JsonSerializable(typeof(RemoveProviderKeyParams))]
[JsonSerializable(typeof(ProvidersAck))]
[JsonSerializable(typeof(ModelProvidersParams))]
[JsonSerializable(typeof(ModelProvidersReport))]
[JsonSerializable(typeof(ModelProviderParams))]
[JsonSerializable(typeof(SaveModelKeyParams))]
[JsonSerializable(typeof(AddCustomProviderParams))]
[JsonSerializable(typeof(SetModelSourceParams))]
[JsonSerializable(typeof(ProviderContribution))]
[JsonSerializable(typeof(WithdrawParams))]
internal sealed partial class ProvidersJson : JsonSerializerContext;