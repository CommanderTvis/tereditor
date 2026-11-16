using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.Vertex.Host;

/// <summary>
/// The Google Vertex AI plugin's host half: contributes the provider to Model providers while the settings are complete, follows every change of them, and
/// withdraws it when the plugin stops. The credentials are the platform's own and never pass through here.
/// </summary>
public sealed class VertexHost : PluginHostModule
{
    public override PluginContract Contract => VertexManifest.Contract;

    public override async ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var providers = context.Dependency(ProvidersContract.Contract);
        // One change at a time, in order, so a quick edit never leaves the older settings contributed.
        var turn = new SemaphoreSlim(1, 1);
        async Task Follow(VertexSettings settings)
        {
            await turn.WaitAsync();
            try
            {
                if (VertexPlatform.Definition(settings) is { } provider) await providers.RequestAsync(ProvidersContract.Contribute, new ProviderContribution(VertexManifest.Id, [provider]));
                else await providers.RequestAsync(ProvidersContract.Withdraw, new WithdrawParams(VertexManifest.Id));
            }
            catch (PluginCallException) { }
            finally { turn.Release(); }
        }
        context.OnSettings<VertexSettings>(settings => _ = Follow(settings));
        await Follow(context.Settings<VertexSettings>());
        return async () =>
        {
            await turn.WaitAsync();
            try { await providers.RequestAsync(ProvidersContract.Withdraw, new WithdrawParams(VertexManifest.Id)); }
            catch (PluginCallException) { }
            finally { turn.Release(); }
        };
    }
}