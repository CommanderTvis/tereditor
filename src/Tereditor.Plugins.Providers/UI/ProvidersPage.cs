using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Providers.UI;

/// <summary>
/// Settings › Model providers: the blocks JetBrains AI, Cloud platforms and Other routers and providers, each a place another plugin can
/// fill, and an action to add a custom provider. Keys are typed into masked fields, sent once and never shown again. Below them
/// stays the older page for the keys Pi and omp read from their own files.
/// </summary>
internal sealed class ProvidersPage : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly StackPanel groups = new() { Spacing = 20, Name = "ModelProviderGroups" };
    private ModelProvidersReport? report;
    private string? adding;
    private bool custom;
    private int generation;

    public ProvidersPage(IPluginUIContext context)
    {
        this.context = context;
        Name = "SettingsModelProviders";
        Spacing = 24;
        var intro = Ui.Text("Add the providers you use. Each agent's own settings page chooses which one it takes. An agent only sees a provider that speaks an API it accepts.", Ui.Muted, 12);
        intro.TextWrapping = TextWrapping.Wrap;
        Children.Add(new StackPanel { Spacing = 4, Children = { Ui.Text("Model providers", Ui.TextBrush, 15), intro } });
        Children.Add(groups);
        Children.Add(new ProvidersView(context));
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
        var rows = 0;
        foreach (var row in report?.Providers.Where(row => row.Provider.Group == group) ?? [])
        {
            box.Children.Add(Row(row));
            rows++;
        }
        // A group nothing fills shows nothing, not even its heading. The slot fills and empties while the page is open.
        if (contributed is Panel panel)
        {
            void Follow() => box.IsVisible = rows > 0 || panel.Children.Count > 0;
            panel.Children.CollectionChanged += (_, _) => Follow();
            Follow();
        }
        groups.Children.Add(box);
    }

    private Control Row(ModelProviderRow row)
    {
        var provider = row.Provider;
        var column = new StackPanel { Spacing = 4, Name = "ModelProvider-" + provider.Id };
        var line = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        DockPanel.SetDock(actions, Dock.Right);
        // A contributed provider is managed by the plugin that contributed it.
        if (provider.Contributor is null)
        {
            var stored = row.Ready && provider.NeedsKey;
            if (stored) actions.Children.Add(Ui.Button("Remove key", () => Load(() => context.RequestAsync(ProvidersContract.RemoveModelKey, new ModelProviderParams(provider.Id)).AsTask())));
            actions.Children.Add(Ui.Button(stored ? "Replace key" : "Add key", () => { adding = adding == provider.Id ? null : provider.Id; custom = false; Render(); }));
            if (provider.Custom) actions.Children.Add(Ui.Button("Remove provider", () => Load(() => context.RequestAsync(ProvidersContract.RemoveCustomProvider, new ModelProviderParams(provider.Id)).AsTask())));
        }
        line.Children.Add(actions);
        var kinds = string.Join(", ", provider.Apis.Select(api => Label(api.Kind)).Distinct());
        line.Children.Add(Ui.Text(provider.Name + (row.Ready ? "  (ready)" : "") + "  ·  " + kinds, Ui.TextBrush, 13));
        column.Children.Add(line);
        if (provider.Note is { } note) column.Children.Add(Hint(note));
        if (provider.OAuth is { Allowed: false, ReasonIfNot: { } reason }) column.Children.Add(Hint(reason));
        if (adding == provider.Id) column.Children.Add(KeyForm(provider));
        return column;
    }

    private static TextBlock Hint(string text)
    {
        var hint = Ui.Text(text, Ui.Muted, 12);
        hint.TextWrapping = TextWrapping.Wrap;
        return hint;
    }

    private Control KeyForm(ProviderDefinition provider)
    {
        var key = new TextBox { PasswordChar = '•', PlaceholderText = "API key", Name = "ModelProviderKey", MinWidth = 280 };
        AutomationProperties.SetName(key, "API key for " + provider.Name);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        void Save()
        {
            var secret = key.Text ?? "";
            key.Text = "";
            if (secret.Trim().Length == 0) return;
            adding = null;
            Load(() => context.RequestAsync(ProvidersContract.SaveModelKey, new SaveModelKeyParams(provider.Id, secret)).AsTask());
        }
        key.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Save(); e.Handled = true; } };
        var save = Ui.Button("Save", Save);
        var cancel = Ui.Button("Cancel", () => { adding = null; Render(); });
        Grid.SetColumn(save, 1);
        Grid.SetColumn(cancel, 2);
        row.Children.Add(key);
        row.Children.Add(save);
        row.Children.Add(cancel);
        return row;
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