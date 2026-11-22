using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Bedrock;
using Tereditor.Plugins.OpenAi;
using Tereditor.Plugins.OpenAi.UI;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.Providers.Host;
using Tereditor.Plugins.Vertex;

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
        Require(ProviderOAuthPolicy.For(ProviderOAuthPolicy.OpenAi, true) is { Allowed: false, Overridable: false, ReasonIfNot: { Length: > 0 } } && ProviderOAuthPolicy.For(ProviderOAuthPolicy.OpenAi, false).Allowed, "in a browser tab the ChatGPT sign-in is off with a reason");
        Require(new[] { BedrockManifest.Manifest, VertexManifest.Manifest }.All(plugin => plugin.UnavailableOn.Contains(HostKind.PureWeb)), "Bedrock and Vertex are refused in the Playground");
        Require(BundledProviders.All.Any(provider => provider.Id == OpenAiProvider.ProviderId && provider.Apis.Any(api => api.Kind == ModelApiKinds.OpenAiResponses)), "the provider the component stores a key for speaks the Responses API");
        Require(new[] { ProvidersManifest.Manifest, OpenAiManifest.Manifest }.Select(plugin => plugin.Category).Distinct().Single() == PluginCategories.ModelProviders, "both are in the Model providers category");

        var state = Path.Combine(root, "openai-store");
        Directory.CreateDirectory(state);
        var keys = new ProviderKeyStore(state);
        List<ProviderDefinition> custom = [];
        List<ModelSourceChoice> sources = [];
        // A fake codex whose login status the check flips: it prints only "Logged in using ChatGPT" or "Not logged in", never a token.
        var fakeCodex = CodexChecks.FakeCodexExecutable(Path.Combine(state, "codex"), Path.Combine(state, "codex-control.json"), Path.Combine(state, "codex-log.txt"));
        void Login(string? how) => CodexChecks.WriteControl(Path.Combine(state, "codex-control.json"), new CodexChecks.FakeControl { Login = how });
        Login(null);
        var service = new ModelProvidersService(keys, new ModelProviderState(() => custom, next => custom = [.. next], () => sources, next => sources = [.. next]), null, cancel => CodexLoginStatus.ReadAsync(fakeCodex, cancel));
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
            IEnumerable<string> Buttons() => view.GetLogicalDescendants().OfType<Button>().Select(button => button.Content as string ?? (button.Content as TextBlock)?.Text ?? "");
            void Click(string label)
            {
                view.GetLogicalDescendants().OfType<Button>().First(button => (button.Content as string ?? (button.Content as TextBlock)?.Text) == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle();
            }
            string Text() => string.Join('\n', view.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").Concat(view.GetLogicalDescendants().OfType<TextBox>().Select(box => box.Text ?? "")));
            Require(Text().Contains("No API key yet", StringComparison.Ordinal) && Buttons().Contains("Add key") && !Buttons().Contains("Remove key") && !view.GetLogicalDescendants().OfType<TextBox>().Any(), "with no key the component shows the shared row with Add key and no inline field");
            Click("Add key");
            One<TextBox>("ModelProviderKey").Text = Sentinel;
            Click("Save");
            Settle();
            Require(Text().Contains("API key stored", StringComparison.Ordinal) && Buttons().Contains("Replace key") && Buttons().Contains("Remove key"), "saving shows the key as stored with Replace key and Remove key");
            Require(!view.GetLogicalDescendants().OfType<TextBox>().Any() && !Text().Contains(Sentinel, StringComparison.Ordinal), "the form closes and the key is never shown");
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

            Click("Remove key");
            Require(Text().Contains("No API key yet", StringComparison.Ordinal) && !File.Exists(Path.Combine(keys.KeysDirectory, "openai")), "removing the key forgets it");

            // The sign-in shows on the card: not signed in on open, waiting after Sign in, signed in once Codex says so, and the provider counts as set up in the list.
            void Until(Func<bool> done, string message)
            {
                try { Tereditor.Checks.E2E.E2eWorkspace.Until(done, message); }
                catch (InvalidOperationException) { Require(false, message); }
            }
            Click("Cancel");
            Require(Text().Contains("Not signed in", StringComparison.Ordinal) && Buttons().Contains("Sign in") && !Buttons().Contains("Sign out"), "on open a Codex that is not signed in shows Sign in");
            Until(() => service.Report().Providers.Single(row => row.Provider.Id == "openai").SignIn is { SignedIn: false }, "the host read the sign-in status when the pane opened");
            var interval = OpenAiView.PollInterval; var throttle = OpenAiView.Throttle;
            OpenAiView.PollInterval = TimeSpan.FromMilliseconds(100); OpenAiView.Throttle = TimeSpan.Zero;
            try
            {
                Click("Sign in");
                Require(Text().Contains("Waiting for sign-in in the browser…", StringComparison.Ordinal) && Buttons().Contains("Cancel") && !Buttons().Contains("Sign in"), "after Sign in the card waits for the browser and offers Cancel");
                Click("Cancel");
                Require(!Text().Contains("Waiting for sign-in", StringComparison.Ordinal) && Buttons().Contains("Sign in"), "Cancel stops waiting");
                Click("Sign in");
                Login("ChatGPT");
                Until(() => Text().Contains("Signed in with ChatGPT", StringComparison.Ordinal), "the poll shows the card signed in once the fake Codex says so");
                Require(Buttons().Contains("Sign out") && !Buttons().Contains("Sign in") && !Text().Contains("Waiting for sign-in", StringComparison.Ordinal), "a signed in card offers Sign out and stops waiting");
                var row = service.Report().Providers.Single(row => row.Provider.Id == "openai");
                Require(row is { Ready: false, SignIn: { SignedIn: true, Method: "ChatGPT" } }, "the list row reports signed in without making the provider ready for a launch");
                Require(row.SignIn!.Method == "ChatGPT" && !Text().Contains("eyJ", StringComparison.Ordinal), "only the kind of sign-in is read, no token");
                // Signed out elsewhere: the next activation of the window reads it again.
                Login(null);
                view.OnActivated();
                Until(() => Text().Contains("Not signed in", StringComparison.Ordinal) && Buttons().Contains("Sign in"), "window activation re-reads the sign-in");
            }
            finally { OpenAiView.PollInterval = interval; OpenAiView.Throttle = throttle; }
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
            if (((PluginMethodSpec)args![0]!).Name == "refreshSignIn") return new ValueTask<ModelProvidersReport>(Task.Run(() => Fake.Service.RefreshSignInAsync((ModelProviderParams)args[1]!)));
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