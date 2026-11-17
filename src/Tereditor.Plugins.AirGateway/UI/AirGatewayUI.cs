using Avalonia.Controls;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.AirGateway.UI;

/// <summary>
/// The agents whose Model source is JetBrains AI (Central), as Model providers last reported them. It is a snapshot read in memory, so building a
/// launch line never waits on the host; it raises <see cref="Changed"/> on the UI thread when the set changes.
/// </summary>
internal sealed class AirChosen
{
    private volatile IReadOnlySet<string> agents = new HashSet<string>();

    public IReadOnlySet<string> Agents => agents;

    public event Action? Changed;

    public void Apply(ModelProvidersReport report)
    {
        var next = AirLaunch.Chosen(report.Sources);
        if (agents.SetEquals(next)) return;
        agents = next;
        Changed?.Invoke();
    }
}

/// <summary>The UI half: Settings › Air Gateway, JetBrains AI (Central) in the Model providers list, the `central run` contribution to the launches of the agents that choose it, and the remaining credits in the header while Central governs an agent.</summary>
public sealed class AirGatewayUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Air Gateway", "", () => new AirSettingsView(context)));
        context.KeymapAction(new("OpenSettings", "Open Air Gateway settings", "Air Gateway", () => context.OpenSettings(UiPages.PluginSection(context.Id))));
        context.SlotSection(new(ProvidersSlots.JetBrainsAi, _ => new JetBrainsAiRow(context)));
        var chosen = new AirChosen();
        // Contributions are read synchronously on the UI thread, so the path Central was found at comes from the host's
        // status channel rather than the file system; until it arrives the bare name is used and the shell resolves it.
        string? centralPath = null;
        context.AgentLaunch(launcherId => AirLaunch.Contribution(chosen.Agents, launcherId, Volatile.Read(ref centralPath)));
        var feed = new AirCreditsFeed(context, chosen);
        context.HeaderItem(new(() => new AirCreditsHeader(context, feed)));
        var providers = context.Dependency(ProvidersContract.Contract);
        var models = providers.Subscribe(ProvidersContract.ModelsChanged, chosen.Apply);
        _ = Task.Run(async () =>
        {
            try { var report = await providers.RequestAsync(ProvidersContract.Models, new ModelProvidersParams()); Dispatcher.UIThread.Post(() => chosen.Apply(report)); }
            catch (Exception error) when (error is not OperationCanceledException) { }
        });
        var subscription = context.Subscribe(AirGatewayContract.Changed, status => { Volatile.Write(ref centralPath, status.Central.Path); feed.Apply(status); }, new AirStatusParams());
        return () => { subscription.Dispose(); models.Dispose(); feed.Dispose(); };
    }
}

/// <summary>JetBrains AI (Central) in Settings › Plugins › Model providers: the agents it works with and where Central is set up. It claims the list place of the provider.</summary>
internal sealed class JetBrainsAiRow : StackPanel
{
    public JetBrainsAiRow(IPluginUIContext context)
    {
        Name = "ModelProvider-" + ProviderCatalog.JetBrainsAi;
        Spacing = 4;
        var agents = ProviderMatching.AgentNames(AirLaunch.Provider, context.Host().Roster.Where(entry => entry.AgentRouting is not null).Select(entry => (entry.Id, entry.Label, entry.AgentRouting)));
        var title = Ui.Text(AirLaunch.Provider.Name + "  ·  Works with " + string.Join(", ", agents.Count > 0 ? agents : AirLaunch.Agents.Select(agent => agent.Label)), Ui.TextBrush, 13);
        title.Name = "JetBrainsAiTitle";
        var note = Ui.Text(AirLaunch.Provider.Note!, Ui.Muted, 12);
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var open = Ui.Button("Open Air Gateway settings", () => context.OpenSettings(UiPages.PluginSection(AirGatewayContract.Id)));
        open.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        Children.Add(title);
        Children.Add(note);
        Children.Add(open);
    }
}