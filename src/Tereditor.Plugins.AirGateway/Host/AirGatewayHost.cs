using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.AirGateway.Host;

/// <summary>
/// The host half: detects the user's Central CLI, adds JetBrains (Air Gateway) to Model providers, and contributes `central run`, and the account those
/// launches run under, to the launches of the agents whose Model source is that provider.
/// </summary>
public sealed class AirGatewayHost : PluginHostModule
{
    public override PluginContract Contract => AirGatewayContract.Contract;

    internal AirEnvironment? Environment { get; init; }

    public override async ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        // The agents whose Model source is JetBrains (Air Gateway), as Model providers last reported. Contributions read this snapshot only.
        IReadOnlySet<string> chosen = new HashSet<string>();
        var runtime = new AirGatewayRuntime(Environment ?? AirEnvironment.Current(), () => Volatile.Read(ref chosen),
            status => context.Publish(AirGatewayContract.Changed, status));
        context.Method(AirGatewayContract.Status, async (parameters, _, cancellationToken) => await runtime.StatusAsync(parameters.Probe, cancellationToken));
        context.Method(AirGatewayContract.SignIn, async (_, _, _) => await runtime.SignInAsync());
        context.Method(AirGatewayContract.SignOut, async (_, _, _) => await runtime.SignOutAsync());
        context.Method(AirGatewayContract.StartProxy, async (_, _, _) => await runtime.StartProxyAsync());
        context.Method(AirGatewayContract.Update, async (_, _, _) => await runtime.UpdateAsync());
        context.Method(AirGatewayContract.AccessSources, async (_, _, cancellationToken) => await runtime.AccessSourcesAsync(cancellationToken));
        context.Method(AirGatewayContract.SwitchAccess, async (parameters, _, _) => await runtime.SwitchAccessAsync(parameters.SourceId));
        // Revived terminals are rebuilt on the host, so the host half contributes too.
        context.AgentLaunch(runtime.Contribution);
        var providers = context.Dependency(ProvidersContract.Contract);
        void Follow(ModelProvidersReport report)
        {
            var next = AirLaunch.Chosen(report.Sources);
            if (Volatile.Read(ref chosen).SetEquals(next)) return;
            Volatile.Write(ref chosen, next);
            _ = runtime.ChosenChangedAsync();
        }
        var subscription = providers.Subscribe(ProvidersContract.ModelsChanged, Follow);
        try
        {
            await providers.RequestAsync(ProvidersContract.Contribute, new ProviderContribution(AirGatewayContract.Id, [AirLaunch.Provider]));
            Follow(await providers.RequestAsync(ProvidersContract.Models, new ModelProvidersParams()));
        }
        catch (PluginCallException) { }
        return async () =>
        {
            subscription.Dispose();
            runtime.Dispose();
            try { await providers.RequestAsync(ProvidersContract.Withdraw, new WithdrawParams(AirGatewayContract.Id)); }
            catch (PluginCallException) { }
        };
    }
}