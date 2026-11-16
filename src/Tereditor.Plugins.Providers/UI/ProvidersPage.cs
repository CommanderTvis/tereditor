using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Providers.UI;

/// <summary>
/// Settings › Plugins › Model providers: the blocks JetBrains AI, Cloud platforms and Other routers and providers, each a place another plugin can
/// fill, and an action to add a custom provider. Keys are typed into masked fields, sent once and never shown again.
/// </summary>
internal sealed class ProvidersPage : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly ModelSourceLaunch launch;
    private readonly StackPanel groups = new() { Spacing = 20, Name = "ModelProviderGroups" };
    private ModelProvidersReport? report;
    private string? adding;
    private bool custom;
    private int generation;

    public ProvidersPage(IPluginUIContext context, ModelSourceLaunch launch)
    {
        this.context = context;
        this.launch = launch;
        Name = "SettingsModelProviders";
        Spacing = 24;
        var intro = Ui.Text("Add the providers you use. Each agent's own settings page chooses which one it takes. An agent only sees a provider that speaks an API it accepts.", Ui.Muted, 12);
        intro.TextWrapping = TextWrapping.Wrap;
        Children.Add(new StackPanel { Spacing = 4, Children = { Ui.Text("Model providers", Ui.TextBrush, 15), intro } });
        Children.Add(groups);
        AttachedToVisualTree += (_, _) => Load(() => context.RequestAsync(ProvidersContract.Models, new ModelProvidersParams()).AsTask());
        Render();
    }

    /// <summary>The label of an API kind, as the pages show it.</summary>
    internal static string Label(string kind) => kind switch
    {
        ModelApiKinds.AnthropicMessages => "Anthropic Messages",
        ModelApiKinds.OpenAiResponses => "OpenAI Responses",
        ModelApiKinds.OpenAiChat => "OpenAI Chat",
        ModelApiKinds.GoogleGenerative => "Google Gemini",
        ModelApiKinds.Vertex => "Vertex AI",
        ModelApiKinds.Bedrock => "Bedrock",
        _ => kind
    };

    private void Load(Func<Task<ModelProvidersReport>> request)
    {
        var mine = ++generation;
        _ = Run();
        async Task Run()
        {
            try
            {
                var next = await request();
                if (mine != generation) return;
                report = next;
                launch.Update(next);
                Render();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (mine == generation) context.Notify(PluginNotificationKind.Error, "Couldn't change the provider", error.Message);
            }
        }
    }

    private void Render()
    {
        groups.Children.Clear();
        AddGroup("JetBrains AI", ProviderGroup.JetBrainsAi, ProvidersSlots.JetBrainsAi);
        AddGroup("Cloud platforms", ProviderGroup.Hyperscalers, ProvidersSlots.Hyperscalers);
        AddGroup("Other routers and providers", ProviderGroup.Other, ProvidersSlots.Other);
        groups.Children.Add(CustomForm());
    }

    private void AddGroup(string heading, ProviderGroup group, string slot)
    {
        var box = new StackPanel { Spacing = 8, Name = "ProviderGroup-" + group };
        box.Children.Add(Ui.Text(heading, Ui.TextBrush, 13));
        var contributed = context.SlotView(slot);
        box.Children.Add(contributed);
        var rows = new List<(string Id, Control Row)>();
        foreach (var row in report?.Providers.Where(row => row.Provider.Group == group) ?? [])
        {
            var control = Row(row);
            box.Children.Add(control);
            rows.Add((row.Provider.Id, control));
        }
        // A group nothing fills shows nothing, not even its heading. The slot fills and empties while the page is open.
        // A plugin that fills the slot with the row of a bundled provider (a control named like it) replaces that row, so the page lists the provider once.
        if (contributed is Panel panel)
        {
            void Follow()
            {
                foreach (var (id, control) in rows) control.IsVisible = !panel.GetLogicalDescendants().OfType<Control>().Any(claimed => claimed.Name == "ModelProvider-" + id);
                box.IsVisible = rows.Count > 0 || panel.Children.Count > 0;
            }
            panel.Children.CollectionChanged += (_, _) => Follow();
            Follow();
        }
        groups.Children.Add(box);
    }

    private Control Row(ModelProviderRow row)
    {
        var provider = row.Provider;
        var column = new ApiKeyRow(provider.Name,
            secret => Load(() => context.RequestAsync(ProvidersContract.SaveModelKey, new SaveModelKeyParams(provider.Id, secret)).AsTask()),
            () => Load(() => context.RequestAsync(ProvidersContract.RemoveModelKey, new ModelProviderParams(provider.Id)).AsTask()))
        { Name = "ModelProvider-" + provider.Id };
        if (provider.Custom && provider.Contributor is null) column.ExtraActions.Add(("Remove provider", () => Load(() => context.RequestAsync(ProvidersContract.RemoveCustomProvider, new ModelProviderParams(provider.Id)).AsTask())));
        if (provider.Note is { } note) column.Hints.Add(note);
        if (provider.OAuth is { Allowed: false, ReasonIfNot: { } reason }) column.Hints.Add(reason);
        var kinds = string.Join(", ", provider.Apis.Select(api => Label(api.Kind)).Distinct());
        column.Show(provider.Name + (row.Ready ? "  (ready)" : "") + "  ·  " + kinds, row.Ready && provider.NeedsKey);
        column.Open = adding == provider.Id;
        column.OpenChanged = open => { adding = open ? provider.Id : null; custom = false; Render(); };
        return column;
    }

    private static readonly string[] CustomKinds = [ModelApiKinds.AnthropicMessages, ModelApiKinds.OpenAiResponses, ModelApiKinds.OpenAiChat, ModelApiKinds.GoogleGenerative];

    private Control CustomForm()
    {
        var panel = new StackPanel { Spacing = 6, Name = "ModelProviderCustom" };
        panel.Children.Add(Ui.Button(custom ? "Hide the custom provider form" : "Add a custom provider", () => { custom = !custom; adding = null; Render(); }));
        if (!custom) return panel;
        var name = new TextBox { PlaceholderText = "name, such as My gateway", Name = "CustomProviderName" };
        var url = new TextBox { PlaceholderText = "base URL, such as https://llm.example.com/v1", Name = "CustomProviderUrl" };
        var key = new TextBox { PasswordChar = '•', PlaceholderText = "API key (leave empty if none)", Name = "CustomProviderKey" };
        AutomationProperties.SetName(key, "API key for the custom provider");
        panel.Children.Add(name);
        panel.Children.Add(url);
        panel.Children.Add(Ui.Text("The APIs this address speaks:", Ui.Muted, 12));
        var boxes = CustomKinds.Select(kind => new CheckBox { Content = Label(kind), Tag = kind, Name = "CustomKind-" + kind }).ToList();
        foreach (var box in boxes) panel.Children.Add(box);
        panel.Children.Add(key);
        void Add()
        {
            var secret = key.Text ?? "";
            key.Text = "";
            var kinds = boxes.Where(box => box.IsChecked == true).Select(box => (string)box.Tag!).ToList();
            custom = false;
            Load(() => context.RequestAsync(ProvidersContract.AddCustomProvider, new AddCustomProviderParams(name.Text ?? "", url.Text ?? "", kinds) { Key = secret.Length > 0 ? secret : null }).AsTask());
        }
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Ui.Button("Add provider", Add) } });
        return panel;
    }
}