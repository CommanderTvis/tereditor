using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Vertex.UI;

/// <summary>
/// The Google Vertex AI component: where the platform's models are (non-secret settings, saved through the host) and whether Claude Code is offered them.
/// It asks for no key and shows none: the credentials are the platform's own.
/// </summary>
internal sealed class VertexView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly TextBox project = new() { PlaceholderText = "Google Cloud project id", MinWidth = 220 };
    private readonly TextBox region = new() { PlaceholderText = "Region, such as global or us-east5", MinWidth = 220 };
    private readonly TextBlock state = Ui.Text("", Ui.Muted, 12);

    public VertexView(IPluginUIContext context)
    {
        this.context = context;
        Name = "VertexComponent";
        // The list names this block and says what it works with while the platform is not set up and no provider backs it.
        AutomationProperties.SetName(this, VertexPlatform.Name);
        var agents = ProviderMatching.AgentNames(VertexPlatform.Definition(new VertexSettings { Project = "tereditor-project" })!, context.Host().Roster.Where(entry => entry.AgentRouting is not null).Select(entry => (entry.Id, entry.Label, entry.AgentRouting)));
        AutomationProperties.SetHelpText(this, agents.Count == 0 ? "No agent takes it yet" : "Works with " + string.Join(", ", agents));
        Spacing = 6;
        state.Name = "VertexState";
        state.TextWrapping = TextWrapping.Wrap;
        project.Name = "VertexProject";
        AutomationProperties.SetName(project, "Google Cloud project id");
        region.Name = "VertexRegion";
        AutomationProperties.SetName(region, "Region, such as global or us-east5");
        Children.Add(Ui.Text(VertexPlatform.Name, Ui.TextBrush, 13));
        var note = Ui.Text(VertexPlatform.Note, Ui.Muted, 12);
        note.TextWrapping = TextWrapping.Wrap;
        Children.Add(note);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(project);
        row.Children.Add(region);
        var save = Ui.Button("Save", Save);
        save.Name = "VertexSave";
        row.Children.Add(save);
        Children.Add(row);
        Children.Add(state);
        Load(context.Settings<VertexSettings>());
        context.OnSettings<VertexSettings>(Load);
    }

    private void Load(VertexSettings settings)
    {
        project.Text = settings.Project;
        region.Text = settings.Region;
        state.Text = VertexPlatform.Problem(settings) ?? "Ready. Claude Code can use Google Vertex AI as its model source.";
    }

    private void Save()
    {
        var next = new VertexSettings { Project = project.Text?.Trim() ?? "", Region = region.Text?.Trim() ?? "" };
        _ = Go();
        async Task Go()
        {
            try { await context.UpdateSettingsAsync(next); }
            catch (Exception error) when (error is not OperationCanceledException) { context.Notify(PluginNotificationKind.Error, "Couldn't save the Google Vertex AI settings", error.Message); }
        }
    }
}