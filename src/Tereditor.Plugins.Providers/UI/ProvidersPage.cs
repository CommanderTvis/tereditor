using Avalonia;
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
/// Settings › Plugins › Model providers: a list of providers on the left (<see cref="MasterDetail"/>, in ProviderOrder, under Set up and Not set up) and the selected
/// provider's pane on the right: what it works with, the key buttons, and the blocks other plugins fill (OpenAI sign-in, JetBrains AI, Bedrock, Vertex) for the
/// provider they name. An Add a custom provider button under the list opens the form in the pane. Keys are typed into masked fields, sent once and never shown again.
/// </summary>
internal sealed class ProvidersPage : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly ModelSourceLaunch launch;
    private readonly MasterDetail master = new("ModelProviders");
    private readonly ContentControl catalog = new() { Name = "ModelProviderPane" };
    private readonly StackPanel blockHost = new() { Name = "ModelProviderBlocks" };
    private readonly List<(string Slot, Control View)> slots = [];
    private ModelProvidersReport? report;
    private string? selectedId;
    private string? paneId;
    private bool paneIsBlock;
    private string? adding;
    private bool custom, focusNew;
    private int generation;

    // One entry of the list: a provider of the report, a plugin block that names one, or a plugin block on its own.
    private sealed record Entry(string Id, string Name, int Rank, ModelProviderRow? Row, Control? Block);

    public ProvidersPage(IPluginUIContext context, ModelSourceLaunch launch)
    {
        this.context = context;
        this.launch = launch;
        Name = "SettingsModelProviders";
        Spacing = 24;
        var intro = Ui.Text("Add the providers you use. Each agent's own settings page chooses which one it takes. An agent only sees a provider that speaks an API it accepts.", Ui.Muted, 12);
        intro.TextWrapping = TextWrapping.Wrap;
        Children.Add(new StackPanel { Spacing = 4, Children = { Ui.Text("Model providers", Ui.TextBrush, 15), intro } });
        Children.Add(master);
        master.Selected += id => { selectedId = id; custom = false; adding = null; RenderList(); };
        master.Detail.Content = new StackPanel { Spacing = 8, Children = { catalog, blockHost } };
        // The slot views live as long as the page, hidden inside the pane. A plugin fills or empties one while the page is open, which changes the list.
        foreach (var slot in new[] { ProvidersSlots.JetBrainsAi, ProvidersSlots.Hyperscalers, ProvidersSlots.Other })
        {
            var view = context.SlotView(slot);
            if (view is Panel panel) panel.Children.CollectionChanged += (_, _) => RenderList();
            slots.Add((slot, view));
            blockHost.Children.Add(view);
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

    /// <summary>What a provider is for, as the page reads it: the agents it works with (not its API kinds).</summary>
    private string WorksWith(ProviderDefinition provider)
    {
        var agents = ProviderMatching.AgentNames(provider, context.Host().Roster.Where(entry => entry.AgentRouting is not null).Select(entry => (entry.Id, entry.Label, entry.AgentRouting)));
        return agents.Count == 0 ? "No agent takes it yet" : "Works with " + string.Join(", ", agents);
    }

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
                if (focusNew)
                {
                    focusNew = false;
                    var known = (report?.Providers ?? []).Select(row => row.Provider.Id).ToHashSet();
                    if (next.Providers.FirstOrDefault(row => !known.Contains(row.Provider.Id)) is { } added) selectedId = added.Provider.Id;
                }
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
        master.Footer.Children.Clear();
        master.Footer.Children.Add(Ui.Button(custom ? "Hide the custom provider form" : "Add a custom provider", () =>
        {
            custom = !custom;
            adding = null;
            if (custom) selectedId = null;
            RenderList(true);
        }));
        RenderList(true);
    }

    // The provider a plugin block stands for: the provider it names ("ModelProvider-<id>", or "<Id>Component"), else the block alone.
    private static string BlockId(Control block, string slot, int index)
    {
        var named = block.GetLogicalDescendants().Prepend(block).OfType<Control>().Select(control => control.Name).FirstOrDefault(name => name?.StartsWith("ModelProvider-", StringComparison.Ordinal) == true);
        if (named is not null) return named[14..];
        if (block.Name is { Length: > 0 } own) return own.EndsWith("Component", StringComparison.Ordinal) ? own[..^9].ToLowerInvariant() : own;
        return "slot:" + slot + ":" + index;
    }

    private List<Entry> Entries()
    {
        var blocks = new Dictionary<string, (string Slot, Control Block)>();
        foreach (var (slot, view) in slots)
            if (view is Panel panel)
                for (var at = 0; at < panel.Children.Count; at++) blocks[BlockId(panel.Children[at], slot, at)] = (slot, panel.Children[at]);
        var entries = new List<Entry>();
        foreach (var row in report?.Providers ?? [])
        {
            var id = row.Provider.Id;
            entries.Add(new(id, row.Provider.Name, ProviderOrder.Rank(id), row, blocks.GetValueOrDefault(id).Block));
            blocks.Remove(id);
        }
        foreach (var (id, (slot, block)) in blocks)
        {
            var name = block.Name is { Length: > 0 } own && own.EndsWith("Component", StringComparison.Ordinal) ? own[..^9] : id.StartsWith("slot:", StringComparison.Ordinal) ? "Provider component" : id;
            entries.Add(new(id, name, Math.Min(ProviderOrder.Rank(id), ProviderOrder.SlotRank(slot)), null, block));
        }
        return [.. entries.OrderBy(entry => entry.Rank).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Fills the list and shows the selected provider's pane. <paramref name="rebuild"/> builds the pane again; a plugin block that fills in or empties leaves the pane being typed in alone.</summary>
    private void RenderList(bool rebuild = false)
    {
        var entries = Entries();
        var rows = new List<Control>();
        foreach (var (title, ready) in new[] { ("Set up", true), ("Not set up", false) })
        {
            var group = entries.Where(entry => (entry.Row?.Ready ?? false) == ready).ToList();
            if (group.Count == 0) continue;
            rows.Add(MasterDetail.Group(ready ? "SetUp" : "NotSetUp", title));
            rows.AddRange(group.Select(ListRow));
        }
        master.SetRows(rows, selectedId, none: custom);
        var previous = selectedId;
        selectedId = custom ? null : master.SelectedId;
        ShowPane(entries, rebuild || previous != selectedId || paneId != selectedId);
    }

    private Control ListRow(Entry entry)
    {
        Control? mark = null;
        if (entry.Row is { } row)
        {
            if (row.Ready)
            {
                mark = Ui.Icon("check", Ui.Success);
                ToolTip.SetTip(mark, row.Provider.NeedsKey ? "Key set" : "Ready");
                AutomationProperties.SetName(mark, row.Provider.NeedsKey ? "key set" : "ready");
            }
            else mark = new TextBlock { Text = "Not set up", FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
        }
        return MasterDetail.Row(entry.Id, Ui.Icon("lock", Ui.Muted), entry.Name, null, entry.Row is null ? null : WorksWith(entry.Row.Provider), null, mark);
    }

    private void ShowPane(List<Entry> entries, bool rebuild)
    {
        var entry = custom ? null : entries.FirstOrDefault(candidate => candidate.Id == selectedId);
        // A plugin block shows through its own slot view: only the block of the selected provider is visible.
        foreach (var (_, view) in slots)
        {
            if (view is not Panel panel) continue;
            foreach (var block in panel.Children) block.IsVisible = entry?.Block == block;
            view.IsVisible = entry?.Block is not null && panel.Children.Contains(entry.Block);
        }
        var showBlock = entry?.Block is not null;
        if (!rebuild && paneId == selectedId && paneIsBlock == showBlock && (custom || catalog.Content is not null || showBlock)) return;
        (paneId, paneIsBlock) = (selectedId, showBlock);
        catalog.Content = custom ? CustomForm() : entry is { Block: null, Row: { } row } ? new Border { Name = "ModelProviderDetail_" + entry.Id, Padding = new Thickness(8, 4, 8, 12), Child = Detail(row) }
            : entry is null ? Ui.Text("Choose a provider in the list.", Ui.Muted, 12) : null;
    }

    /// <summary>The pane of a provider that has no block of its own: its name, the agents it works with, why a sign-in is not offered, and the key buttons.</summary>
    private Control Detail(ModelProviderRow row)
    {
        var provider = row.Provider;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = provider.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBrush });
        var works = Ui.Text(WorksWith(provider), Ui.Muted, 12);
        works.Name = "ModelProviderWorksWith";
        panel.Children.Add(works);
        var column = new ApiKeyRow(provider.Name,
            secret => Load(() => context.RequestAsync(ProvidersContract.SaveModelKey, new SaveModelKeyParams(provider.Id, secret)).AsTask()),
            () => Load(() => context.RequestAsync(ProvidersContract.RemoveModelKey, new ModelProviderParams(provider.Id)).AsTask()))
        { Name = "ModelProvider-" + provider.Id };
        if (provider.Custom && provider.Contributor is null) column.ExtraActions.Add(("Remove provider", () => Load(() => context.RequestAsync(ProvidersContract.RemoveCustomProvider, new ModelProviderParams(provider.Id)).AsTask())));
        if (provider.Note is { } note) column.Hints.Add(note);
        if (provider.OAuth is { Allowed: false, ReasonIfNot: { } reason }) column.Hints.Add(reason);
        var stored = row.Ready && provider.NeedsKey;
        column.Show(row.Ready ? provider.NeedsKey ? "Key stored" : "Ready" : provider.NeedsKey ? "No key yet" : "Not ready", stored);
        column.Open = adding == provider.Id;
        column.OpenChanged = open => adding = open ? provider.Id : null;
        panel.Children.Add(column);
        return panel;
    }

    private static readonly string[] CustomKinds = [ModelApiKinds.AnthropicMessages, ModelApiKinds.OpenAiResponses, ModelApiKinds.OpenAiChat, ModelApiKinds.GoogleGenerative];

    private Control CustomForm()
    {
        var panel = new StackPanel { Spacing = 6, Name = "ModelProviderCustom" };
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
            focusNew = true;
            Load(() => context.RequestAsync(ProvidersContract.AddCustomProvider, new AddCustomProviderParams(name.Text ?? "", url.Text ?? "", kinds) { Key = secret.Length > 0 ? secret : null }).AsTask());
        }
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Ui.Button("Add provider", Add) } });
        return panel;
    }
}