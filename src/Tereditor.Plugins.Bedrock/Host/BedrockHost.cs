using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.Bedrock.Host;

/// <summary>
/// The Amazon Bedrock plugin's host half: contributes the provider to Model providers while the settings are complete, follows every change of them, and
/// withdraws it when the plugin stops. The credentials are the platform's own and never pass through here.
/// </summary>
public sealed class BedrockHost : PluginHostModule
{
    public override PluginContract Contract => BedrockManifest.Contract;

    public override async ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var providers = context.Dependency(ProvidersContract.Contract);
        // One change at a time, in order, so a quick edit never leaves the older settings contributed.
        var turn = new SemaphoreSlim(1, 1);
        async Task Follow(BedrockSettings settings)
        {
            await turn.WaitAsync();
            try
            {
                if (BedrockPlatform.Definition(settings) is { } provider) await providers.RequestAsync(ProvidersContract.Contribute, new ProviderContribution(BedrockManifest.Id, [provider]));
                else await providers.RequestAsync(ProvidersContract.Withdraw, new WithdrawParams(BedrockManifest.Id));
            }
            catch (PluginCallException) { }
            finally { turn.Release(); }
        }
        context.OnSettings<BedrockSettings>(settings => _ = Follow(settings));
        await Follow(context.Settings<BedrockSettings>());
        return async () =>
        {
            await turn.WaitAsync();
            try { await providers.RequestAsync(ProvidersContract.Withdraw, new WithdrawParams(BedrockManifest.Id)); }
            catch (PluginCallException) { }
            finally { turn.Release(); }
        };
    }
}