using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.OpenAi;
using Tereditor.Plugins.OpenAi.UI;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.Providers.Host;

namespace Tereditor.Checks;

// The OpenAI plugin: its manifest, and its component mounted headless over the real key store. The key goes in through Model providers and is never
// shown again; Sign in runs Codex's own login in a terminal of the active workspace, and refuses without one.
internal static class OpenAiChecks
{
    private const string Sentinel = "sk-openai-sentinel-5d2c";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("openai: " + message);
    }

    internal static void Run(string root)
    {
        var manifest = OpenAiManifest.Manifest;
        Require(manifest is { Category: PluginCategories.ModelProviders, Host: null, Headless: HeadlessSupport.No } && manifest.DependsOn.Single() is { Id: "providers" } dependency && dependency.WireVersion == ProvidersContract.WireVersion,
            "the manifest is a Model providers plugin with a UI half that depends on Model providers at its wire version");
        Require(OpenAiProvider.Policy.Allowed && OpenAiProvider.SignInText.Contains("Codex keeps its own sign-in", StringComparison.Ordinal) && OpenAiProvider.SignInCommand == "codex login", "the policy is allowed and the sign-in is Codex's own");
        Require(BundledProviders.All.Any(provider => provider.Id == OpenAiProvider.ProviderId && provider.Apis.Any(api => api.Kind == ModelApiKinds.OpenAiResponses)), "the provider the component stores a key for speaks the Responses API");
        Require(new[] { ProvidersManifest.Manifest, OpenAiManifest.Manifest }.Select(plugin => plugin.Category).Distinct().Single() == PluginCategories.ModelProviders, "both are in the Model providers category");

        var state = Path.Combine(root, "openai-store");
        Directory.CreateDirectory(state);
        var keys = new ProviderKeyStore(state);
        List<ProviderDefinition> custom = [];
        List<ModelSourceChoice> sources = [];
        var service = new ModelProvidersService(keys, new ModelProviderState(() => custom, next => custom = [.. next], () => sources, next => sources = [.. next]));
        var fake = Fake.Create(service);
        var window = new Window { Width = 700, Height = 500 };
        var view = new OpenAiView(fake);
        window.Content = view;
        window.Show();
        void Settle() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        try
        {
            Settle();
            T One<T>(string name) where T : Control => view.GetLogicalDescendants().Prepend(view).OfType<T>().Single(control => control.Name == name);
            string Text() => string.Join('\n', view.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").Concat(view.GetLogicalDescendants().OfType<TextBox>().Select(box => box.Text ?? "")));
            Require(One<TextBlock>("OpenAiKeyState").Text!.StartsWith("No API key", StringComparison.Ordinal) && !One<Button>("OpenAiRemove").IsVisible, "with no key the component asks for one");
            One<TextBox>("OpenAiKey").Text = Sentinel;
            One<Button>("OpenAiSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle();
            Require(One<TextBlock>("OpenAiKeyState").Text!.StartsWith("API key stored", StringComparison.Ordinal) && One<Button>("OpenAiRemove").IsVisible, "saving shows the key as stored");
            Require(One<TextBox>("OpenAiKey").Text is null or "" && !Text().Contains(Sentinel, StringComparison.Ordinal), "the key field is cleared and the key is never shown");
            Require(File.ReadAllText(Path.Combine(keys.KeysDirectory, "openai")) == Sentinel && service.Report().Providers.Single(row => row.Provider.Id == "openai").Ready, "the key is in Model providers' key store");
            Require(Fake.Terminals.Count == 0, "saving a key opens no terminal");

            // Sign in: refused without an active workspace, otherwise Codex's own login in that workspace.
            One<Button>("OpenAiSignIn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle();
            Require(Fake.Terminals.Count == 0 && Fake.Notices.Count == 1, "Sign in without a workspace opens no terminal and says why");
            Fake.Workspace = "w1";
            One<Button>("OpenAiSignIn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle();
            Require(Fake.Terminals is [("w1", "codex login")], "Sign in runs codex login in the active workspace");

            One<Button>("OpenAiRemove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle();
            Require(One<TextBlock>("OpenAiKeyState").Text!.StartsWith("No API key", StringComparison.Ordinal) && !File.Exists(Path.Combine(keys.KeysDirectory, "openai")), "removing the key forgets it");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS openai: manifest, key saved and removed without showing it, Sign in runs codex login");
    }

    // A fake plugin UI context: what the component calls, answered by the real service.
    public class Fake : DispatchProxy
    {
        private static ModelProvidersService service = null!;
        internal static ModelProvidersService Service => service;
        internal static string? Workspace { get; set; }
        internal static List<(string Workspace, string? Command)> Terminals { get; } = [];
        internal static List<string> Notices { get; } = [];

        internal static IPluginUIContext Create(ModelProvidersService backing)
        {
            service = backing;
            Workspace = null;
            Terminals.Clear();
            Notices.Clear();
            return Create<IPluginUIContext, Fake>();
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Id": return OpenAiManifest.Id;
                case "Dependency": return Create<IPluginDependencyHandle, Handle>();
                case "Host":
                    return new PluginHostProjection([], new Dictionary<string, IReadOnlyList<HostWorkspace>>(), Workspace, null, null, new PluginAppSettings("t", false, 80, false, 80, false, []),
                        new Dictionary<string, IReadOnlyList<TerminalTabInfo>>(), new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, int>(), [], null);
                case "OpenTerminalAsync":
                    Terminals.Add(((string)args![0]!, ((TerminalOpenOptions?)args[1])?.Command));
                    return ValueTask.FromResult("tab");
                case "Notify": Notices.Add(string.Join(" / ", args!.OfType<string>())); return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    public class Handle : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != "RequestAsync") throw new NotSupportedException(method.Name);
            object? result = ((PluginMethodSpec)args![0]!).Name switch
            {
                "models" => Fake.Service.Report(),
                "saveModelKey" => Fake.Service.SaveKey((SaveModelKeyParams)args[1]!),
                "removeModelKey" => Fake.Service.RemoveKey((ModelProviderParams)args[1]!),
                var other => throw new NotSupportedException(other)
            };
            return Activator.CreateInstance(method.ReturnType, result);
        }
    }
}