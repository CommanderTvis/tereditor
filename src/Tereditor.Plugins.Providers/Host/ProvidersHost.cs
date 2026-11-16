using System.Text.Json.Serialization.Metadata;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Providers.Host;

/// <summary>
/// The Model providers plugin's host half: the key store, the merged provider list, each agent's model source, and the launch
/// contribution that lets Pi and omp start with the chosen provider.
/// </summary>
public sealed class ProvidersHost : PluginHostModule
{
    public override PluginContract Contract => ProvidersContract.Contract;

    public override IJsonTypeInfoResolver Json => ProvidersHostJson.Default;

    public override async ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var root = ProviderKeyStore.DefaultRoot();
        var keys = new ProviderKeyStore(root);
        var models = new ModelProvidersService(keys, new ModelProviderState(
            () => context.ReadState<List<ProviderDefinition>>("customProviders", []),
            custom => context.WriteState("customProviders", custom.ToList()),
            () => context.ReadState<List<ModelSourceChoice>>("modelSources", []),
            sources => context.WriteState("modelSources", sources.ToList())), new AgentModelFiles(keys, root));
        context.Method(ProvidersContract.Models, (_, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(models.Report, ct)));
        context.Method(ProvidersContract.SaveModelKey, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.SaveKey(p), ct)));
        context.Method(ProvidersContract.RemoveModelKey, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.RemoveKey(p), ct)));
        context.Method(ProvidersContract.AddCustomProvider, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.AddCustom(p), ct)));
        context.Method(ProvidersContract.RemoveCustomProvider, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.RemoveCustom(p), ct)));
        context.Method(ProvidersContract.SetModelSource, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.SetSource(p), ct)));
        context.Method(ProvidersContract.Contribute, (p, _, _) => ValueTask.FromResult(models.Contribute(p)));
        context.Method(ProvidersContract.Withdraw, (p, _, ct) => new ValueTask<ProvidersAck>(Task.Run(() => models.Withdraw(p), ct)));
        // The contributor reads a snapshot only; the files are written when a source is chosen and here, off the caller's thread.
        context.AgentLaunch(models.Launch);
        await Task.Run(models.Sync);
        return null;
    }
}