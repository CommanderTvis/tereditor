using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Providers.UI;

/// <summary>
/// The model source control an agent's settings page embeds: the agent's own provider (the default, tereditor does not step in) or
/// one of the providers the user added that speaks an API the agent accepts. The choice is recorded in this plugin's own state.
/// It does not change how the agent starts yet.
/// </summary>
internal sealed class ModelSourceView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly string agentId;
    private readonly ComboBox choice = new() { Name = "ModelSourceChoice", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly TextBlock hint = Ui.Text("", Ui.Muted, 12);
    private bool applying;
    private int generation;

    public ModelSourceView(IPluginUIContext context, string agentId)
    {
        this.context = context;
        this.agentId = agentId;
        Name = "ModelSource-" + agentId;
        Spacing = 4;
        AutomationProperties.SetName(choice, "Model source");
        hint.TextWrapping = TextWrapping.Wrap;
        Children.Add(Ui.Text("Model source", Ui.TextBrush, 13));
        Children.Add(choice);
        Children.Add(hint);
        choice.SelectionChanged += (_, _) =>
        {
            if (applying || choice.SelectedItem is not ComboBoxItem { Tag: var tag }) return;
            Save(tag as string);
        };
        AttachedToVisualTree += (_, _) => Load(() => context.RequestAsync(ProvidersContract.Models, new ModelProvidersParams()).AsTask());
    }

    /// <summary>The ready providers <paramref name="routing"/>'s agent is offered, in list order.</summary>
    internal static IReadOnlyList<ModelProviderRow> Offered(AgentRouting? routing, IReadOnlyList<ModelProviderRow> rows) =>
        routing is null ? [] : [.. rows.Where(row => row.Ready && ProviderMatching.Offered(routing, row.Provider))];

    private void Save(string? providerId) =>
        Load(() => context.RequestAsync(ProvidersContract.SetModelSource, new SetModelSourceParams(agentId, providerId)).AsTask());

    private void Load(Func<Task<ModelProvidersReport>> request)
    {
        var mine = ++generation;
        _ = Run();
        async Task Run()
        {
            try
            {
                var report = await request();
                if (mine == generation) Show(report);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (mine == generation) context.Notify(PluginNotificationKind.Error, "Couldn't change the model source", error.Message);
            }
        }
    }

    private void Show(ModelProvidersReport report)
    {
        var routing = context.Host().Roster.FirstOrDefault(entry => entry.Id == agentId)?.AgentRouting;
        var offered = Offered(routing, report.Providers);
        var chosen = report.Sources.FirstOrDefault(source => source.AgentId == agentId)?.ProviderId;
        var entries = new List<(string Name, string? Id)> { ("Use the agent's own provider", null) };
        entries.AddRange(offered.Select(row => (row.Provider.Name, (string?)row.Provider.Id)));
        if (chosen is not null && offered.All(row => row.Provider.Id != chosen)) entries.Add((chosen + " (no longer fits this agent)", chosen));
        applying = true;
        // The list is rebuilt only when it changed: this runs inside the selection event that saved the choice.
        if (!choice.Items.OfType<ComboBoxItem>().Select(item => ((string)item.Content!, (string?)item.Tag)).SequenceEqual(entries))
        {
            choice.Items.Clear();
            foreach (var (name, id) in entries) choice.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        }
        choice.SelectedIndex = Math.Max(0, entries.FindIndex(entry => entry.Id == chosen));
        applying = false;
        hint.Text = routing is null ? "This agent has no provider choice."
            : offered.Count == 0 && chosen is null ? "No provider you added fits this agent yet. Add one in Settings › Model providers."
            : "The choice is saved. It does not change how the agent starts yet.";
    }
}
