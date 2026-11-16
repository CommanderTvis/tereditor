using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

public sealed record ProvidersAck(bool Ok = true);

/// <summary>One provider in the merged list (bundled, contributed and custom) and whether an agent can use it now.</summary>
/// <param name="Provider">The definition.</param>
/// <param name="Ready">A key is stored, or the provider needs none.</param>
public sealed record ModelProviderRow(ProviderDefinition Provider, bool Ready);

/// <summary>The agent whose model comes from <paramref name="ProviderId"/>. An agent with no row uses its own provider.</summary>
public sealed record ModelSourceChoice(string AgentId, string ProviderId)
{
    /// <summary>The one model to list for a provider the agent does not know by id and that names no models; null uses the provider's first model, else <c>default</c>.</summary>
    public string? Model { get; init; }
}

/// <summary>The merged provider list and the model source each agent has. It never carries a key.</summary>
public sealed record ModelProvidersReport(IReadOnlyList<ModelProviderRow> Providers, IReadOnlyList<ModelSourceChoice> Sources);

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
    public const int WireVersion = 3;

    public static readonly PluginMethod<ModelProvidersParams, ModelProvidersReport> Models = new("models");
    public static readonly PluginMethod<SaveModelKeyParams, ModelProvidersReport> SaveModelKey = new("saveModelKey");
    public static readonly PluginMethod<ModelProviderParams, ModelProvidersReport> RemoveModelKey = new("removeModelKey");
    public static readonly PluginMethod<AddCustomProviderParams, ModelProvidersReport> AddCustomProvider = new("addCustomProvider");
    public static readonly PluginMethod<ModelProviderParams, ModelProvidersReport> RemoveCustomProvider = new("removeCustomProvider");
    public static readonly PluginMethod<SetModelSourceParams, ModelProvidersReport> SetModelSource = new("setModelSource");
    public static readonly PluginMethod<ProviderContribution, ProvidersAck> Contribute = new("contribute");
    public static readonly PluginMethod<WithdrawParams, ProvidersAck> Withdraw = new("withdraw");

    public static readonly PluginContract Contract = PluginContract.Create<ProvidersSettings>(ProvidersManifest.Id, WireVersion,
        [Models, SaveModelKey, RemoveModelKey, AddCustomProvider, RemoveCustomProvider, SetModelSource, Contribute, Withdraw], [], ProvidersJson.Default);
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
[JsonSerializable(typeof(SetModelSourceParams))]
[JsonSerializable(typeof(ProviderContribution))]
[JsonSerializable(typeof(WithdrawParams))]
internal sealed partial class ProvidersJson : JsonSerializerContext;