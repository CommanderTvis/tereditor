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
            sources => context.WriteState("modelSources", sources.ToList())), new AgentModelFiles(keys, root), cancel => CodexLoginStatus.ReadAsync("codex", cancel));
        // Every change is pushed, and the Codex launch contribution reads the latest report from memory: a launch never touches a file.
        ModelProvidersReport? latest = null;
        ModelProvidersReport Publish(ModelProvidersReport report)
        {
            Volatile.Write(ref latest, report);
            context.Publish(ProvidersContract.ModelsChanged, report);
            return report;
        }
        ValueTask<ModelProvidersReport> Report(Func<ModelProvidersReport> change, CancellationToken ct) => new(Task.Run(() => Publish(change()), ct));
        context.Method(ProvidersContract.Models, (_, _, ct) => Report(models.Report, ct));
        context.Method(ProvidersContract.SaveModelKey, (p, _, ct) => Report(() => models.SaveKey(p), ct));
        context.Method(ProvidersContract.RemoveModelKey, (p, _, ct) => Report(() => models.RemoveKey(p), ct));
        context.Method(ProvidersContract.AddCustomProvider, (p, _, ct) => Report(() => models.AddCustom(p), ct));
        context.Method(ProvidersContract.RemoveCustomProvider, (p, _, ct) => Report(() => models.RemoveCustom(p), ct));
        context.Method(ProvidersContract.SetModelSource, (p, _, ct) => Report(() => models.SetSource(p), ct));
        context.Method(ProvidersContract.RefreshSignIn, (p, _, ct) => new ValueTask<ModelProvidersReport>(Task.Run(async () => Publish(await models.RefreshSignInAsync(p, ct)), ct)));
        context.Method(ProvidersContract.Contribute, (p, _, ct) => new ValueTask<ProvidersAck>(Task.Run(() => { var ack = models.Contribute(p); Publish(models.Report()); return ack; }, ct)));
        context.Method(ProvidersContract.Withdraw, (p, _, ct) => new ValueTask<ProvidersAck>(Task.Run(() => { var ack = models.Withdraw(p); Publish(models.Report()); return ack; }, ct)));
        // The contributors read a snapshot only: Pi and omp from the files written when a source is chosen and here, Codex from the latest report.
        context.AgentLaunch(models.Launch);
        context.AgentLaunch(launcherId => ProviderLaunch.For(launcherId, Volatile.Read(ref latest)));
        await Task.Run(() => { models.Sync(); Publish(models.Report()); });
        return null;
    }
}