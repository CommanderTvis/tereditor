using System.Text.Json.Serialization.Metadata;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Providers.Host;

/// <summary>The Model providers plugin's host half: it edits the credential files of Pi and omp on this host.</summary>
public sealed class ProvidersHost : PluginHostModule
{
    public override PluginContract Contract => ProvidersContract.Contract;

    public override IJsonTypeInfoResolver Json => ProvidersHostJson.Default;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var service = new ProvidersService(ProvidersService.DefaultFiles(
            () => context.ReadState<List<CustomProvider>>("custom", []),
            custom => context.WriteState("custom", custom.ToList())));
        var keys = new ProviderKeyStore(ProviderKeyStore.DefaultRoot());
        var models = new ModelProvidersService(keys, new ModelProviderState(
            () => context.ReadState<List<ProviderDefinition>>("customProviders", []),
            custom => context.WriteState("customProviders", custom.ToList()),
            () => context.ReadState<List<ModelSourceChoice>>("modelSources", []),
            sources => context.WriteState("modelSources", sources.ToList())));
        context.Method(ProvidersContract.Models, (_, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(models.Report, ct)));
        context.Method(ProvidersContract.SaveModelKey, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.SaveKey(p), ct)));
        context.Method(ProvidersContract.RemoveModelKey, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.RemoveKey(p), ct)));
        context.Method(ProvidersContract.AddCustomProvider, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.AddCustom(p), ct)));
        context.Method(ProvidersContract.RemoveCustomProvider, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.RemoveCustom(p), ct)));
        context.Method(ProvidersContract.SetModelSource, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(() => models.SetSource(p), ct)));
        context.Method(ProvidersContract.Contribute, (p, _, _) => ValueTask.FromResult(models.Contribute(p)));
        context.Method(ProvidersContract.Withdraw, (p, _, ct) => new ValueTask<ProvidersAck>(Task.Run(() => models.Withdraw(p), ct)));
        // Reads and writes touch files, so none of them runs on the caller's thread.
        context.Method(ProvidersContract.List, (p, _, ct) => new ValueTask<ProvidersReport>(Task.Run(() => service.List(p.Agent), ct)));
        context.Method(ProvidersContract.SetKey, (p, _, ct) => new ValueTask<ProvidersReport>(Task.Run(() => service.SetKey(p), ct)));
        context.Method(ProvidersContract.RemoveKey, (p, _, ct) => new ValueTask<ProvidersReport>(Task.Run(() => service.RemoveKey(p), ct)));
        return ValueTask.FromResult<PluginDisposer?>(null);
    }
}