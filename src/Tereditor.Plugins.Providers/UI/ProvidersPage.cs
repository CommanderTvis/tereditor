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
/// Settings › Plugins › Model providers: one list of providers in ProviderOrder, with the blocks other plugins fill
/// (OpenAI sign-in, JetBrains AI, Bedrock, Vertex) at their place in it, and an action to add a custom provider. Keys are typed into masked fields, sent once and never shown again.
/// </summary>
internal sealed class ProvidersPage : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly ModelSourceLaunch launch;
    private readonly StackPanel groups = new() { Spacing = 20, Name = "ModelProviderGroups" };
    private readonly StackPanel listing = new() { Spacing = 8, Name = "ModelProviderList" };
    private readonly List<(string Id, string Name, Control Row)> rows = [];
    private readonly List<(string Slot, Control View)> slots = [];
    private bool arranging, rearrange;
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
        // The slot views live as long as the page. A plugin fills or empties one while the page is open, which reorders the list.
        foreach (var slot in new[] { ProvidersSlots.JetBrainsAi, ProvidersSlots.Hyperscalers, ProvidersSlots.Other })
        {
            var view = context.SlotView(slot);
            if (view is Panel panel) panel.Children.CollectionChanged += (_, _) => Arrange();
            slots.Add((slot, view));
        }
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
        listing.Children.Clear();
        rows.Clear();
        foreach (var row in report?.Providers ?? []) rows.Add((row.Provider.Id, row.Provider.Name, Row(row)));
        Arrange();
        groups.Children.Add(listing);
        groups.Children.Add(CustomForm());
    }

    // The one list: catalog rows and plugin blocks together, in ProviderOrder, by name within a rank. A plugin block that names a provider
    // ("ModelProvider-<id>") takes that provider's rank and replaces its catalog row, so the page lists the provider once.
    private void Arrange()
    {
        // Adding a slot view to the list can fill it at once, which asks to arrange again: run the pass again after this one, never inside it.
        if (arranging) { rearrange = true; return; }
        arranging = true;
        try { do { rearrange = false; ArrangeOnce(); } while (rearrange); }
        finally { arranging = false; }
    }

    private void ArrangeOnce()
    {
        var claimed = slots.SelectMany(slot => slot.View.GetLogicalDescendants().OfType<Control>()).Select(control => control.Name)
            .Where(name => name?.StartsWith("ModelProvider-", StringComparison.Ordinal) == true).Select(name => name![14..]).ToHashSet();
        var entries = new List<(int Rank, string Name, Control Control)>();
        foreach (var (id, name, control) in rows)
        {
            control.IsVisible = !claimed.Contains(id);
            entries.Add((ProviderOrder.Rank(id), name, control));
        }
        foreach (var (slot, view) in slots)
        {
            var own = view.GetLogicalDescendants().OfType<Control>().Select(control => control.Name).Where(name => name?.StartsWith("ModelProvider-", StringComparison.Ordinal) == true)
                .Select(name => ProviderOrder.Rank(name![14..])).DefaultIfEmpty(ProviderOrder.SlotRank(slot)).Min();
            view.IsVisible = view is not Panel panel || panel.Children.Count > 0;
            entries.Add((own, "", view));
        }
        // Leave the list alone when it already reads right: moving a slot view out and in makes it fill again, which would ask for another pass without end.
        var ordered = entries.OrderBy(entry => entry.Rank).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Control).ToList();
        if (listing.Children.SequenceEqual(ordered)) return;
        listing.Children.Clear();
        foreach (var control in ordered) listing.Children.Add(control);
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