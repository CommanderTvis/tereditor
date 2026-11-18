using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.AirGateway;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.ClaudeCode;
using Tereditor.Plugins.Codex;
using Tereditor.Plugins.Omp;
using Tereditor.Plugins.Pi;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.Providers.Host;
using Tereditor.Plugins.Providers.UI;
using Tereditor.UI.Plugins;

namespace Tereditor.Checks;

// Settings › Plugins › Model providers and the model source slot, headless over a fake plugin context backed by the real service: the provider rail in popularity order with its Set up and Not set up groups, the selected provider's pane and key states,
// contributed components placed in that list, adding a key and a custom provider, the slot's choice per agent filtered by the agent's
// declaration and recorded, and the slot with Model providers off, which is only a hint.
internal static class ProvidersUiChecks
{
    private const string Sentinel = "sk-ui-sentinel-71d2";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("providers-ui: " + message);
    }

    internal static void Run(string root)
    {
        var state = Path.Combine(root, "providers-ui");
        Directory.CreateDirectory(state);
        List<ProviderDefinition> custom = [];
        List<ModelSourceChoice> sources = [];
        var service = new ModelProvidersService(new ProviderKeyStore(state), new ModelProviderState(() => custom, next => custom = [.. next], () => sources, next => sources = [.. next]));
        var registry = new PluginRegistry();
        var manifests = new[] { ClaudeCodeManifest.Manifest, CodexManifest.Manifest, PiManifest.Manifest, OmpManifest.Manifest };
        var roster = manifests.Select(manifest => new PluginRosterEntry(manifest.Id, manifest.Label, manifest.Icon, manifest.Version, 1, PluginOrigin.Builtin, PluginStatus.Active) { AgentRouting = manifest.AgentRouting }).ToArray();
        var context = Fake.Create(service, registry, roster);
        var window = new Window { Width = 700, Height = 900 };
        var host = new StackPanel();
        window.Content = new ScrollViewer { Content = host };
        window.Show();
        void Settle() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        try
        {
            // Model providers off: an agent page's slot is only the hint, and the Providers page is not there at all.
            var off = UiSlots.ModelSourceView(context, "claude-code");
            host.Children.Add(off);
            Settle();
            Require(Find<TextBlock>(off, "PluginSlotHint").SingleOrDefault()?.Text == UiSlots.ModelSourceHint && Find<ComboBox>(off, "ModelSourceChoice").Count == 0, "with Model providers off the slot shows only the hint");
            Require(registry.SettingsSections.Count == 0, "with Model providers off there is no page");

            // On: the plugin registers its page and a section for every agent.
            registry.SetActive(ProvidersManifest.Id, true);
            new ProvidersUI().Activate(context);
            Settle();
            Require(registry.SettingsSections.Count == 1 && registry.SlotSections.Count == 4, "Model providers registers its page and one model source section per agent");
            // The Codex seam on the UI half: nothing before the first report, nothing with no source, the contribution once Codex has one.
            Require(Fake.Launch?.Invoke("codex") is null, "before a report the UI half adds nothing to the Codex launch");
            Fake.Push!(service.Report());
            Require(Fake.Launch!("codex") is null, "with no source chosen the UI half adds nothing to the Codex launch");
            service.SaveKey(new("openrouter", Sentinel));
            Fake.Push!(service.SetSource(new("codex", "openrouter")));
            Require(Fake.Launch!("codex") is { Arguments.Count: > 0 } && Fake.Launch!("claude") is null, "a chosen source reaches the Codex launch from the UI half only");
            Fake.Push!(service.SetSource(new("codex", null)));
            service.RemoveKey(new("openrouter"));
            Require(Fake.Launch!("codex") is null, "clearing the source clears the contribution");
            Require(Find<ComboBox>(off, "ModelSourceChoice").Count == 1 && Find<TextBlock>(off, "PluginSlotHint").Count == 0, "turning it on fills the slot already on screen, no hint left");

            var page = registry.SettingsSections.Single().Value.Create();
            host.Children.Add(page);
            Settle();
            // The page is a master-detail: a rail of providers and the pane of the selected one, with the first provider selected.
            var rail = Find<ListBox>(page, "ModelProvidersRail").Single();
            string[] Order() => [.. ((IEnumerable<Control>)rail.ItemsSource!).Select(item => item.Tag as string).OfType<string>()];
            void Select(string id) { rail.SelectedItem = ((IEnumerable<Control>)rail.ItemsSource!).First(item => item.Tag as string == id); Settle(); }
            Require(!Texts(page).Any(text => text.Contains("Cloud platforms", StringComparison.Ordinal)), "the page has no Cloud platforms heading");
            Require(Find<TextBlock>(page, "MasterGroup_NotSetUp").Count == 1 && Find<TextBlock>(page, "MasterGroup_SetUp").Count == 0, "with no key set the rail has only the Not set up heading");
            Require(Order() is ["openai", "anthropic", "openrouter", "together", "fireworks", ..], "the rail follows the popularity order, OpenAI and Anthropic first, then the routers");
            Require(Texts(Find<Grid>(page, "MasterRow_openrouter").Single()).Any(text => text.StartsWith("Works with ", StringComparison.Ordinal)), "a rail row says which agents the provider works with");
            Require(Find<StackPanel>(page, "ModelProvider-openai").Single().IsVisible && Find<StackPanel>(page, "ModelProvider-openrouter").Count == 0, "only the selected provider has its pane");
            Select("anthropic");
            Require(Texts(Find<Border>(page, "ModelProviderDetail_anthropic").Single()).Any(text => text.Contains("does not allow a Claude subscription", StringComparison.Ordinal)), "Anthropic shows why a subscription sign-in is not offered");
            Require(Texts(page).Contains("No key yet") && Buttons(page).Contains("Add key") && !Buttons(page).Contains("Remove key") && Find<TextBlock>(page, "ModelProviderWorksWith").Single().Text!.StartsWith("Works with Claude Code", StringComparison.Ordinal), "an Anthropic pane with no key says so, offers Add key and names the agents it works with");

            // A contributed component takes its place in the list: JetBrains AI after Anthropic, before the routers.
            registry.SetActive("air-gateway", true);
            registry.AddSlotSection("air-gateway", new(ProvidersSlots.JetBrainsAi, _ => new TextBlock { Name = "ModelProvider-jetbrains-ai", Text = "Central sign-in" }));
            Settle();
            Require(Order() is ["openai", "anthropic", "jetbrains-ai", "openrouter", ..], "a contributed JetBrains AI component sits between Anthropic and the routers");
            Select("jetbrains-ai");
            Require(Find<TextBlock>(page, "ModelProvider-jetbrains-ai").Single().IsVisible && Find<StackPanel>(page, "ModelProviderBlocks").Single().IsVisible, "selecting the component shows it in the pane");
            Select("anthropic");
            Require(!Find<TextBlock>(page, "ModelProvider-jetbrains-ai").Single().IsEffectivelyVisible, "selecting another provider hides the component");

            // A plugin block named like a bundled provider replaces that provider's row, so the page lists it once.
            Select("openai");
            Require(Find<StackPanel>(page, "ModelProvider-openai").Single().IsVisible, "the bundled OpenAI API pane shows while no plugin fills in for it");
            registry.AddSlotSection("air-gateway", new(ProvidersSlots.Other, _ => new StackPanel { Name = "ModelProvider-openai" }));
            Settle();
            Require(Find<StackPanel>(page, "ModelProvider-openai").Count(entry => entry.IsEffectivelyVisible) == 1 && Order()[0] == "openai" && Order().Count(id => id == "openai") == 1, "a plugin block for OpenAI replaces the bundled OpenAI API pane, with one row that stays first");
            registry.AddSlotSection("air-gateway", new(ProvidersSlots.Hyperscalers, _ => new StackPanel { Name = "ModelProvider-bedrock" }));
            Settle();
            Require(Array.IndexOf(Order(), "bedrock") > Array.IndexOf(Order(), "fireworks") && Array.IndexOf(Order(), "bedrock") < Array.IndexOf(Order(), "deepseek"), "a cloud platform block sits after the routers and before the rest of the catalog");

            // Add a key: the form takes it masked, clears it, and nothing on the page shows it.
            Select("openrouter");
            Require(Texts(page).Contains("No key yet"), "a provider with no key says so");
            Click(page, "Add key", Find<StackPanel>(page, "ModelProvider-openrouter").Single());
            Settle();
            var key = Find<TextBox>(page, "ModelProviderKey").Single();
            Require(key.PasswordChar == '•', "the key field is masked");
            key.Text = Sentinel;
            Click(page, "Save", page);
            Settle();
            Require(Find<TextBox>(page, "ModelProviderKey").Count == 0 && Texts(page).Contains("Key stored") && Buttons(page).Contains("Replace key") && Buttons(page).Contains("Remove key"), "a saved key makes the provider ready and the form goes");
            Require(Find<TextBlock>(page, "MasterGroup_SetUp").Count == 1 && Order()[0] == "openrouter" && Find<Grid>(page, "MasterRow_openrouter").Single().GetLogicalDescendants().OfType<Control>().Any(mark => ToolTip.GetTip(mark) is "Key set") && Find<ListBox>(page, "ModelProvidersRail").Single().SelectedItem is Control { Tag: "openrouter" }, "the provider moves under Set up with a key mark and stays selected");
            Require(!AllText(page).Contains("sentinel", StringComparison.Ordinal), "no control shows the key");

            // Add a custom provider.
            Click(page, "Add a custom provider", page);
            Settle();
            Require(rail.SelectedItem is null && Find<TextBox>(page, "CustomProviderName").Count == 1, "the custom provider form opens in the pane with no provider selected");
            Find<TextBox>(page, "CustomProviderName").Single().Text = "Test gateway";
            Find<TextBox>(page, "CustomProviderUrl").Single().Text = "https://llm.example.com/v1";
            Find<CheckBox>(page, "CustomKind-" + ModelApiKinds.AnthropicMessages).Single().IsChecked = true;
            Click(page, "Add provider", page);
            Settle();
            Require(rail.SelectedItem is Control { Tag: "custom-test-gateway" } && Find<TextBox>(page, "CustomProviderName").Count == 0, "the new custom provider is selected and the form is gone");
            var row = Find<Border>(page, "ModelProviderDetail_custom-test-gateway").Single();
            Require(Texts(row).Any(text => text.Contains("Works with Claude Code", StringComparison.Ordinal) && !text.Contains("Anthropic Messages", StringComparison.Ordinal)) && Buttons(row).Contains("Remove provider"), "the custom provider is listed with the agents it works with, not its API kinds, and can be removed");

            // The red dot: only a provider an agent uses while it has no key. A provider nobody uses and with no key is just Not set up.
            Require(Find<Avalonia.Controls.Shapes.Ellipse>(page, "MasterProblem_anthropic").Count == 0 && Find<Avalonia.Controls.Shapes.Ellipse>(page, "MasterProblem_together").Count == 0, "a provider with no key that nobody uses has no red dot");
            host.Children.Remove(page);
            sources = [new("codex", "together")];
            var second = registry.SettingsSections.Single().Value.Create();
            host.Children.Add(second);
            Settle();
            var dot = Find<Avalonia.Controls.Shapes.Ellipse>(second, "MasterProblem_together").Single();
            Require(ToolTip.GetTip(dot) is "Codex uses this provider, but its key is missing." && Find<Avalonia.Controls.Shapes.Ellipse>(second, "MasterProblem_anthropic").Count == 0 && Find<Avalonia.Controls.Shapes.Ellipse>(second, "MasterProblem_openrouter").Count == 0, "a provider an agent uses without a key has a red dot that says why, and no other row has one");
            var secondRail = Find<ListBox>(second, "ModelProvidersRail").Single();
            secondRail.SelectedItem = ((IEnumerable<Control>)secondRail.ItemsSource!).First(item => item.Tag as string == "together");
            Settle();
            Require(Find<TextBlock>(second, "ModelProviderProblem").Single().Text!.StartsWith("Codex uses this provider, but its key is missing.", StringComparison.Ordinal) && Buttons(second).Contains("Add key"), "the pane of that provider repeats the sentence and offers Add key");
            host.Children.Remove(second);
            sources = [];
            Settle();

            // The slot: own provider first, then the user's providers that fit the agent.
            ComboBox Choice(string agent)
            {
                var view = UiSlots.ModelSourceView(context, agent);
                host.Children.Add(view);
                Settle();
                return Find<ComboBox>(view, "ModelSourceChoice").Single();
            }
            string[] Names(ComboBox box) => [.. box.Items.OfType<ComboBoxItem>().Select(item => (string)item.Content!)];
            var claude = Choice("claude-code");
            Require(Names(claude).SequenceEqual(["Use the agent's own provider", "OpenRouter", "Test gateway"]) && claude.SelectedIndex == 0, "Claude Code is offered providers that speak the Anthropic API, with its own provider chosen by default: " + string.Join(", ", Names(claude)));
            var codex = Choice("codex");
            Require(Names(codex).SequenceEqual(["Use the agent's own provider", "OpenRouter"]), "Codex is not offered a provider that speaks only the Anthropic API: " + string.Join(", ", Names(codex)));
            claude.SelectedIndex = 2;
            Settle();
            Require(sources is [{ AgentId: "claude-code", ProviderId: "custom-test-gateway" }], "choosing a provider records it for the agent");
            claude.SelectedIndex = 0;
            Settle();
            Require(sources.Count == 0, "choosing the agent's own provider clears the record");

            // A provider its contributor wires to agents itself is offered to exactly those agents, and no other.
            service.Contribute(new("air-gateway", [AirLaunch.Provider]));
            foreach (var wired in new[] { "claude-code", "codex", "pi" }) Require(Names(Choice(wired)).Contains("JetBrains AI (Central)"), wired + " is offered JetBrains AI (Central)");
            Require(!Names(Choice("omp")).Contains("JetBrains AI (Central)"), "omp is not offered JetBrains AI (Central)");
            service.Withdraw(new("air-gateway"));

            // Off again: the slot goes back to the hint alone, and the record of what was chosen stays in the plugin's own state.
            claude.SelectedIndex = 1;
            Settle();
            registry.SetActive(ProvidersManifest.Id, false);
            Settle();
            Require(Find<ComboBox>(off, "ModelSourceChoice").Count == 0 && Find<TextBlock>(off, "PluginSlotHint").SingleOrDefault()?.Text == UiSlots.ModelSourceHint, "turning Model providers off leaves only the hint in the agent's slot");
            Require(sources is [{ AgentId: "claude-code", ProviderId: "openrouter" }], "the recorded source stays with the plugin that owns it");
        }
        finally { window.Close(); }
        ModelSourceFirst();
        Console.WriteLine("PASS providers-ui: master-detail rail in popularity order, contributed component, key and custom provider forms, model source slot, Providers off, Model source first on agent pages");
    }

    // Convention: Model source is the first setting of every agent page, right after the page's heading block.
    private static void ModelSourceFirst()
    {
        if (BoundaryChecks.Checkout() is not { } checkout) { Console.WriteLine("SKIP model source order: no source checkout"); return; }
        foreach (var page in new[] { "ClaudeCode/UI/ClaudeCodeSettingsSection.axaml", "Codex/UI/CodexSettingsView.axaml", "Pi/UI/PiSettingsView.axaml", "Omp/UI/OmpSettingsView.axaml" })
        {
            var text = File.ReadAllText(Path.Combine(checkout, "src", "Tereditor.Plugins." + page.Replace('/', Path.DirectorySeparatorChar)));
            var host = text.IndexOf("x:Name=\"ModelSourceHost\"", StringComparison.Ordinal);
            Require(host > 0, page + " embeds the model source");
            var inputs = new[] { "<TextBox", "<ComboBox", "<CheckBox", "<AutoCompleteBox", "<Button" };
            Require(inputs.All(input => text.IndexOf(input, StringComparison.Ordinal) is var at && (at < 0 || at > host)), page + ": Model source comes before every other setting");
        }
    }

    private static List<T> Find<T>(Control root, string name) where T : Control => [.. root.GetLogicalDescendants().Prepend(root).OfType<T>().Where(control => control.Name == name)];

    private static IEnumerable<string> Texts(ILogical root) => root.GetLogicalDescendants().Prepend(root).OfType<TextBlock>().Select(block => block.Text ?? "");

    private static string AllText(Control root) => string.Join('\n', Texts(root).Concat(root.GetLogicalDescendants().OfType<TextBox>().Select(box => box.Text ?? "")));

    private static IEnumerable<string> Buttons(Control root) => root.GetLogicalDescendants().OfType<Button>().Select(button => string.Concat(Texts(button)));

    private static void Click(Control page, string label, Control scope)
    {
        var button = scope.GetLogicalDescendants().OfType<Button>().FirstOrDefault(candidate => string.Concat(Texts(candidate)) == label) ?? throw new InvalidOperationException("providers-ui: no button " + label);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    // A fake plugin UI context: the calls the page and the slot make, answered by the real service, and the registry the app would hold.
    public class Fake : DispatchProxy
    {
        private static ModelProvidersService service = null!;
        private static PluginRegistry registry = null!;
        private static IReadOnlyList<PluginRosterEntry> roster = [];
        internal static Func<string, AgentLaunchContribution?>? Launch { get; private set; }
        internal static Action<ModelProvidersReport>? Push { get; private set; }

        internal static IPluginUIContext Create(ModelProvidersService backing, PluginRegistry plugins, IReadOnlyList<PluginRosterEntry> entries)
        {
            (service, registry, roster) = (backing, plugins, entries);
            return Create<IPluginUIContext, Fake>();
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Id": return ProvidersManifest.Id;
                case "Launchers": return Array.Empty<AgentLauncher>();
                case "Host":
                    return new PluginHostProjection([], new Dictionary<string, IReadOnlyList<HostWorkspace>>(), null, null, null, new PluginAppSettings("t", false, 80, false, 80, false, []),
                        new Dictionary<string, IReadOnlyList<TerminalTabInfo>>(), new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, int>(), roster, null);
                case "Subscribe": Push = (Action<ModelProvidersReport>)args![1]!; return new Stop();
                case "OnLaunchersChanged":
                case "WatchHost": return new Stop();
                case "SettingsSection": registry.AddSettingsSection(ProvidersManifest.Id, (SettingsSectionRegistration)args![0]!); return null;
                case "AgentLaunch":
                    // The page registers Pi's and omp's contribution and Codex's in turn; the Codex one is read back by the check below.
                    Launch = (Func<string, AgentLaunchContribution?>)args![0]!;
                    registry.AddLaunchContributor(ProvidersManifest.Id, Launch);
                    return null;
                case "SlotSection": registry.AddSlotSection(ProvidersManifest.Id, (SlotSectionRegistration)args![0]!); return null;
                case "SlotView": return new PluginSlot(registry, (string)args![0]!, (string?)args[1], bare: true, (string?)args[2]);
                case "Notify": throw new InvalidOperationException("providers-ui: the page reported an error: " + string.Join(" / ", args!.OfType<string>()));
                case "RequestAsync":
                    {
                        object? result = ((PluginMethodSpec)args![0]!).Name switch
                        {
                            "models" => service.Report(),
                            "saveModelKey" => service.SaveKey((SaveModelKeyParams)args[1]!),
                            "removeModelKey" => service.RemoveKey((ModelProviderParams)args[1]!),
                            "addCustomProvider" => service.AddCustom((AddCustomProviderParams)args[1]!),
                            "removeCustomProvider" => service.RemoveCustom((ModelProviderParams)args[1]!),
                            "setModelSource" => service.SetSource((SetModelSourceParams)args[1]!),
                            var other => throw new NotSupportedException(other)
                        };
                        return Activator.CreateInstance(method.ReturnType, result);
                    }
                default: throw new NotSupportedException(method.Name);
            }
        }

        private sealed class Stop : IDisposable
        {
            public void Dispose() { }
        }
    }
}