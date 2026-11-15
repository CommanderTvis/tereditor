using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Tereditor.Host.Abstractions;
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

// Settings › Model providers and the model source slot, headless over a fake plugin context backed by the real service: the three groups,
// a contributed component replacing a group's hint, adding a key and a custom provider, the slot's choice per agent filtered by the agent's
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
            Require(Find<ComboBox>(off, "ModelSourceChoice").Count == 1 && Find<TextBlock>(off, "PluginSlotHint").Count == 0, "turning it on fills the slot already on screen, no hint left");

            var page = registry.SettingsSections.Single().Value.Create();
            host.Children.Add(page);
            Settle();
            var groups = new[] { ProviderGroup.JetBrainsAi, ProviderGroup.Hyperscalers, ProviderGroup.Other }.Select(group => Find<StackPanel>(page, "ProviderGroup-" + group).Single()).ToArray();
            Require(Texts(groups[0]).Any(text => text.Contains("JetBrains AI appears here", StringComparison.Ordinal)) && Texts(groups[1]).Any(text => text.Contains("Hyperscalers", StringComparison.Ordinal) || text.Contains("Vertex AI", StringComparison.Ordinal)),
                "the JetBrains AI and Hyperscalers groups are empty places with a hint");
            Require(Find<StackPanel>(groups[2], "ModelProvider-openrouter").Count == 1 && Find<StackPanel>(groups[2], "ModelProvider-anthropic").Count == 1, "bundled providers are listed under Other routers and providers");
            Require(Texts(page).Any(text => text.Contains("Claude subscription", StringComparison.Ordinal) || text.Contains("does not allow a Claude subscription", StringComparison.Ordinal)), "Anthropic shows why a subscription sign-in is not offered");

            // A contributed component fills its group and removes the hint.
            registry.SetActive("air-gateway", true);
            registry.AddSlotSection("air-gateway", new(ProvidersSlots.JetBrainsAi, _ => new TextBlock { Name = "AirComponent", Text = "Central sign-in" }));
            Settle();
            Require(Find<TextBlock>(page, "AirComponent").Count == 1 && !Texts(groups[0]).Any(text => text.Contains("appears here", StringComparison.Ordinal)), "a contributed component replaces the group's hint");

            // Add a key: the form takes it masked, clears it, and nothing on the page shows it.
            Click(page, "Add key", Find<StackPanel>(page, "ModelProvider-openrouter").Single());
            Settle();
            var key = Find<TextBox>(page, "ModelProviderKey").Single();
            Require(key.PasswordChar == '•', "the key field is masked");
            key.Text = Sentinel;
            Click(page, "Save", page);
            Settle();
            Require(Find<TextBox>(page, "ModelProviderKey").Count == 0 && Texts(page).Any(text => text.StartsWith("OpenRouter  (ready)", StringComparison.Ordinal)), "a saved key makes the provider ready and the form goes");
            Require(!AllText(page).Contains("sentinel", StringComparison.Ordinal), "no control shows the key");

            // Add a custom provider.
            Click(page, "Add a custom provider", page);
            Settle();
            Find<TextBox>(page, "CustomProviderName").Single().Text = "Test gateway";
            Find<TextBox>(page, "CustomProviderUrl").Single().Text = "https://llm.example.com/v1";
            Find<CheckBox>(page, "CustomKind-" + ModelApiKinds.AnthropicMessages).Single().IsChecked = true;
            Click(page, "Add provider", page);
            Settle();
            var row = Find<StackPanel>(page, "ModelProvider-custom-test-gateway").Single();
            Require(Texts(row).Any(text => text.Contains("Anthropic Messages", StringComparison.Ordinal)) && Buttons(row).Contains("Remove provider"), "the custom provider is listed with its API and can be removed");

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

            // Off again: the slot goes back to the hint alone, and the record of what was chosen stays in the plugin's own state.
            claude.SelectedIndex = 1;
            Settle();
            registry.SetActive(ProvidersManifest.Id, false);
            Settle();
            Require(Find<ComboBox>(off, "ModelSourceChoice").Count == 0 && Find<TextBlock>(off, "PluginSlotHint").SingleOrDefault()?.Text == UiSlots.ModelSourceHint, "turning Model providers off leaves only the hint in the agent's slot");
            Require(sources is [{ AgentId: "claude-code", ProviderId: "openrouter" }], "the recorded source stays with the plugin that owns it");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS providers-ui: groups, contributed component, key and custom provider forms, model source slot, Providers off");
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
                case "OnLaunchersChanged":
                case "WatchHost": return new Stop();
                case "SettingsSection": registry.AddSettingsSection(ProvidersManifest.Id, (SettingsSectionRegistration)args![0]!); return null;
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
                            "list" => new ProvidersReport(((ProvidersParams)args[1]!).Agent, "fixture", []),
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
