using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Providers.UI;

/// <summary>The Model providers plugin's UI half: one Settings page for model providers, and the model source control agent pages embed.</summary>
public sealed class ProvidersUI : PluginUIModule
{
    // Agents that exist whenever their plugin is installed; other agent plugins that declare what they accept are added from the roster.
    private static readonly string[] BuiltinAgents = ["claude-code", "codex", "pi", "omp"];

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var launch = new ModelSourceLaunch();
        // The launch line is built on the UI side too, so omp's profile is contributed here from the sources the pages last showed.
        context.AgentLaunch(launch.For);
        _ = Task.Run(async () =>
        {
            try { launch.Update(await context.RequestAsync(ProvidersContract.Models, new ModelProvidersParams())); }
            catch (Exception error) when (error is not OperationCanceledException) { }
        });
        context.SettingsSection(new("Model providers", ProvidersManifest.Icon, () => new ProvidersPage(context, launch)));
        var agents = BuiltinAgents.Concat(context.Host().Roster.Where(entry => entry.AgentRouting is not null).Select(entry => entry.Id)).Distinct();
        foreach (var agent in agents)
            context.SlotSection(new(UiSlots.ModelSource(agent), _ => new ModelSourceView(context, agent, launch)));
        // The Codex launch contribution reads the latest report from memory; the host pushes it after every change.
        ModelProvidersReport? latest = null;
        context.AgentLaunch(launcherId => ProviderLaunch.For(launcherId, Volatile.Read(ref latest)));
        var subscription = context.Subscribe(ProvidersContract.ModelsChanged, report => Volatile.Write(ref latest, report), new ModelProvidersParams());
        return subscription.Dispose;
    }
}