using System.Diagnostics;
using System.Text.Json;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.ClaudeCode;
using Tereditor.Plugins.Codex;
using Tereditor.Plugins.Omp;
using Tereditor.Plugins.Pi;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.Providers.Host;
using Tereditor.Plugins.Providers.UI;

namespace Tereditor.Checks;

// The Model providers plugin: keys for Pi (auth.json) and omp (the tereditor profile's .env) are written without disturbing
// what else the files hold, are never returned, and a Claude subscription sign-in is never offered.
internal static class ProvidersChecks
{
    private const string Secret = "sk-fixture-9f3a7c2b";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("providers: " + message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("providers: " + message);
    }

    internal static void Run(string root)
    {
        var manifest = ProvidersManifest.Manifest;
        Require(manifest.UnavailableOn.Count == 0, "The keys are files of the host's disk, which a browser tab has too.");

        var directory = Path.Combine(root, "providers");
        Directory.CreateDirectory(directory);
        var auth = Path.Combine(directory, "pi", "auth.json");
        var env = Path.Combine(directory, "omp", ".env");
        var remembered = new List<CustomProvider>();
        var environment = new Dictionary<string, string?> { ["GROQ_API_KEY"] = "from-environment" };
        var service = new ProvidersService(new ProviderFiles(() => auth, () => env, () => remembered, custom => { remembered = [.. custom]; }, name => environment.GetValueOrDefault(name)));

        // Pi: a sign-in Pi stored stays untouched, a key lands beside it, a Claude subscription is called out.
        Directory.CreateDirectory(Path.GetDirectoryName(auth)!);
        File.WriteAllText(auth, """{"openai-codex":{"type":"oauth","access":"a","refresh":"r"},"anthropic":{"type":"oauth","access":"a","refresh":"r"}}""");
        var report = service.List(ProviderAgent.Pi);
        Require(report.Providers.Single(row => row.Id == "anthropic") is { Source: ProviderSource.SignIn, Note: { } warn } && warn.Contains("not supported", StringComparison.Ordinal), "A Claude subscription sign-in is flagged as unsupported.");
        Require(report.Providers.Single(row => row.Id == "openai-codex") is { Source: ProviderSource.SignIn, Custom: true }, "A sign-in the catalog does not list is still shown.");
        Require(report.Providers.Single(row => row.Id == "groq").Source == ProviderSource.Environment, "A key in the host's environment is shown as such.");

        report = service.SetKey(new(ProviderAgent.Pi, "openai", Secret));
        Require(report.Providers.Single(row => row.Id == "openai").Source == ProviderSource.Key, "A stored key makes the provider connected.");
        Require(!JsonSerializer.Serialize(report).Contains(Secret, StringComparison.Ordinal), "A report never carries a key.");
        var written = JsonDocument.Parse(File.ReadAllText(auth)).RootElement;
        Require(written.GetProperty("openai").GetProperty("key").GetString() == Secret && written.GetProperty("openai").GetProperty("type").GetString() == "api_key", "Pi's file holds the key as Pi reads it.");
        Require(written.GetProperty("openai-codex").GetProperty("type").GetString() == "oauth", "The sign-in beside the key is untouched.");
        if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(auth) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "The file is readable by its owner only.");

        report = service.SetKey(new(ProviderAgent.Pi, "anthropic", Secret));
        Require(report.Providers.Single(row => row.Id == "anthropic") is { Source: ProviderSource.Key }, "An API key replaces a subscription sign-in when the user chooses so.");
        report = service.RemoveKey(new(ProviderAgent.Pi, "openai"));
        Require(report.Providers.Single(row => row.Id == "openai").Source == ProviderSource.None && !File.ReadAllText(auth).Contains("\"openai\"", StringComparison.Ordinal), "Removing a key takes its entry out.");

        report = service.SetKey(new(ProviderAgent.Pi, "my-gateway", Secret) { Name = "My gateway" });
        Require(report.Providers.Single(row => row.Id == "my-gateway") is { Custom: true, Name: "My gateway", Source: ProviderSource.Key }, "A provider the lists do not name can be added for Pi.");
        service.RemoveKey(new(ProviderAgent.Pi, "my-gateway"));
        Require(remembered.Count == 0 && service.List(ProviderAgent.Pi).Providers.All(row => row.Id != "my-gateway"), "Removing a custom provider forgets it.");

        Throws(() => service.SetKey(new(ProviderAgent.Pi, "Bad Id", Secret)), "A provider id is validated.");
        Throws(() => service.SetKey(new(ProviderAgent.Pi, "openai", "  ")), "An empty key is refused.");
        Throws(() => service.SetKey(new(ProviderAgent.Pi, "openai", "a\nb")), "A key is one line.");

        // omp: a variable in the profile's .env; every other line stays.
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        File.WriteAllText(env, "# my settings\nOTHER=1\nexport OPENAI_API_KEY=old\n");
        report = service.SetKey(new(ProviderAgent.Omp, "openai", "new value #1"));
        var lines = File.ReadAllLines(env);
        Require(lines.Contains("OTHER=1") && lines.Contains("# my settings") && lines.Count(line => line.Contains("OPENAI_API_KEY", StringComparison.Ordinal)) == 1 && lines.Contains("OPENAI_API_KEY=\"new value #1\""), "omp's key replaces the variable's line, quoted, and keeps the other lines.");
        Require(report.Providers.Single(row => row.Id == "openai").Source == ProviderSource.Key && !JsonSerializer.Serialize(report).Contains("new value", StringComparison.Ordinal), "omp's report shows the key as stored and never returns it.");
        Require(report.Note is { Length: > 0 }, "The report says which omp launches read the keys.");
        service.SetKey(new(ProviderAgent.Omp, "mistral", Secret));
        Require(File.ReadAllLines(env).Contains("MISTRAL_API_KEY=" + Secret), "A catalog provider uses its documented variable.");
        service.RemoveKey(new(ProviderAgent.Omp, "mistral"));
        Require(!File.ReadAllText(env).Contains("MISTRAL", StringComparison.Ordinal) && File.ReadAllText(env).Contains("OTHER=1", StringComparison.Ordinal), "Removing a key deletes only its line.");

        Throws(() => service.SetKey(new(ProviderAgent.Omp, "my-gateway", Secret)), "omp needs the variable for a provider the catalog does not know.");
        Throws(() => service.SetKey(new(ProviderAgent.Omp, "my-gateway", Secret) { EnvVar = "lower" }), "A variable name is validated.");
        report = service.SetKey(new(ProviderAgent.Omp, "my-gateway", Secret) { Name = "Gateway", EnvVar = "MY_GATEWAY_API_KEY" });
        Require(report.Providers.Single(row => row.Id == "my-gateway") is { Custom: true, EnvVar: "MY_GATEWAY_API_KEY", Source: ProviderSource.Key }, "A custom omp provider is remembered with its variable.");
        Require(service.List(ProviderAgent.Omp).Providers.Single(row => row.Id == "anthropic").Note == ProviderCatalog.AnthropicNote && service.List(ProviderAgent.Omp).Providers.All(row => row.Source != ProviderSource.SignIn), "Anthropic is offered by API key only, with the reason, and omp rows never show a sign-in.");
        Require(ProviderCatalog.Pi.Concat(ProviderCatalog.Omp).All(provider => ProviderCatalog.IsProviderId(provider.Id) && ProviderCatalog.IsEnvVar(provider.EnvVar)), "Every catalog entry has a valid id and variable.");

        // The page offers Pi always, and omp unless its plugin is refused.
        static AgentRosterEntry Entry(string id, AgentPluginState state, bool? installed = null) => new(id, id, id, id, state, installed, installed == false ? id + " was not found." : null);
        Require(ProvidersView.Offered([Entry("pi", AgentPluginState.Active, true), Entry("omp", AgentPluginState.Active)]) == (true, true), "Both agents are offered when both plugins are on, installed or undecided.");
        Require(ProvidersView.Offered([Entry("pi", AgentPluginState.Active), Entry("omp", AgentPluginState.Disabled)]) == (true, true) && ProvidersView.Offered([Entry("pi", AgentPluginState.Disabled), Entry("omp", AgentPluginState.Active)]) == (true, true),
            "A disabled plugin's agent is still offered, so the page is open whatever plugins are on.");
        Require(ProvidersView.Offered([Entry("pi", AgentPluginState.Active), Entry("omp", AgentPluginState.Refused)]) == (true, false) && ProvidersView.Offered([]) == (true, true), "A refused omp plugin hides omp; Pi and an absent roster still offer both agents.");
        Require(ProvidersView.Offered([Entry("pi", AgentPluginState.Active, false), Entry("omp", AgentPluginState.Active, true)]) == (true, true), "An agent whose program was not found is still offered.");
        Require(ProvidersView.Intro(true, false).Contains("Pi uses", StringComparison.Ordinal) && !ProvidersView.Intro(true, false).Contains("omp", StringComparison.Ordinal) &&
            ProvidersView.Intro(false, true).Contains("omp uses", StringComparison.Ordinal), "The opening sentence names only the agents offered.");
        Model(root);
    }

    private static AgentRouting Routing(string pluginId) => pluginId switch
    {
        "claude-code" => ClaudeCodeManifest.Manifest.AgentRouting!,
        "codex" => CodexManifest.Manifest.AgentRouting!,
        "pi" => PiManifest.Manifest.AgentRouting!,
        _ => OmpManifest.Manifest.AgentRouting!
    };

    // The Codex launch seam: nothing chosen is byte-identical, a chosen source names the helper and the id and never the key, and other agents are untouched.
    private static void CodexSeam(ModelProvidersService service, ProviderKeyStore keys, string sentinel, Func<List<ModelSourceChoice>> sources)
    {
        const string Baseline = "codex";
        static string Line(params AgentLaunchContribution?[] contributions) => CodexLaunch.Line("codex", new(false, false) { Contributions = [.. contributions.OfType<AgentLaunchContribution>()] });
        Require(Line() == Baseline && Line(ProviderLaunch.For("codex", null)) == Baseline, "With no report the Codex line is the agent's own.");
        var report = service.Report();
        Require(report.Sources.All(choice => choice.AgentId != "codex") && ProviderLaunch.For("codex", report) is null && Line(ProviderLaunch.For("codex", report)) == Baseline, "With no source chosen the Codex line is byte-identical.");

        report = service.SetSource(new("codex", "openrouter"));
        var contribution = ProviderLaunch.For("codex", report) ?? throw new InvalidOperationException("providers: a chosen source contributes to the Codex launch.");
        var words = string.Join('\n', contribution.Arguments);
        Require(contribution.BlockedReason is null && contribution.Wrapper.Count == 0 && !contribution.ReplacesExecutable && contribution.Environment.Count == 0, "The contribution only adds arguments.");
        Require(words.Contains("model_provider='tereditor-openrouter'", StringComparison.Ordinal) && words.Contains("model_providers.tereditor-openrouter.wire_api='responses'", StringComparison.Ordinal) &&
            words.Contains("model_providers.tereditor-openrouter.base_url='https://openrouter.ai/api/v1'", StringComparison.Ordinal) &&
            words.Contains("model_providers.tereditor-openrouter.auth.command='" + keys.HelperPath + "'", StringComparison.Ordinal) && words.Contains("auth.args=['openrouter']", StringComparison.Ordinal), "The launch names the provider, its Responses endpoint and the key helper.");
        var line = Line(contribution);
        Require(!line.Contains(sentinel, StringComparison.Ordinal) && !words.Contains(sentinel, StringComparison.Ordinal) && !PluginJson.Serialize(report).Contains(sentinel, StringComparison.Ordinal), "The key is in no launch word and no report.");
        Require(line.StartsWith("codex -c ", StringComparison.Ordinal), "The words follow the executable.");
        if (!OperatingSystem.IsWindows())
        {
            using var shell = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "codex() { printf '%s\\n' \"$@\"; }; " + line]) { RedirectStandardOutput = true, UseShellExecute = false })!;
            var output = shell.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            shell.WaitForExit();
            Require(output.SequenceEqual(contribution.Arguments), "The shell hands Codex each word literally.");
        }
        Require(new[] { "claude", "pi", "omp", "other" }.All(launcher => ProviderLaunch.For(launcher, report) is null), "Only Codex launches change.");

        // A provider with no key needs no helper; a name with a quote loses the quote; an unusable source blocks the launch.
        report = service.AddCustom(new("It's local", "http://localhost:1234/v1", [ModelApiKinds.OpenAiResponses]));
        var local = report.Providers.Single(row => row.Provider.Custom);
        report = service.SetSource(new("codex", local.Provider.Id));
        var keyless = ProviderLaunch.For("codex", report)!;
        Require(keyless.BlockedReason is null && keyless.Arguments.Contains("model_providers.tereditor-" + local.Provider.Id + ".name='Its local'") && !string.Join(' ', keyless.Arguments).Contains("auth", StringComparison.Ordinal), "A keyless provider has no auth command.");
        var notReady = new ModelProvidersReport([local with { Ready = false }], [new("codex", local.Provider.Id)]) { Helper = keys.HelperPath };
        var noApi = new ModelProvidersReport([new(BundledProviders.All.Single(provider => provider.Id == "anthropic"), true)], [new("codex", "anthropic")]) { Helper = keys.HelperPath };
        var noHelper = new ModelProvidersReport(service.Report().Providers.Where(row => row.Provider.Id == "openrouter").ToArray(), [new("codex", "openrouter")]);
        Require(new[] { notReady, noApi, noHelper }.All(blocked => ProviderLaunch.For("codex", blocked)?.BlockedReason is { Length: > 0 }), "A source Codex cannot use blocks the launch.");
        Require(Line(ProviderLaunch.For("codex", notReady)).StartsWith("sh -c", StringComparison.Ordinal), "A blocked Codex line prints the reason and exits.");
        service.RemoveCustom(new(local.Provider.Id));
        Require(sources().All(choice => choice.AgentId != "codex"), "Removing the provider returns Codex to its own.");
    }

    // The model source work: what each agent declares, which providers it is offered, the key store and its helper, custom providers and the recorded source.
    private static void Model(string root)
    {
        // Declarations: each agent says what it accepts and none depends on Model providers.
        Require(Routing("claude-code").Accepts is [{ Kind: ModelApiKinds.AnthropicMessages, Injection: AgentInjection.SettingsFile }], "Claude Code accepts only the Anthropic Messages API, through a settings file.");
        Require(Routing("codex").Accepts is [{ Kind: ModelApiKinds.OpenAiResponses, Injection: AgentInjection.ConfigOverride }], "Codex accepts only the OpenAI Responses API, through configuration overrides.");
        Require(Routing("pi").Accepts.All(api => api.Injection == AgentInjection.ModelsFile) && Routing("omp").Accepts.All(api => api.Injection == AgentInjection.ModelsFile), "Pi and omp take models files.");
        Require(new[] { ClaudeCodeManifest.Manifest, CodexManifest.Manifest, PiManifest.Manifest, OmpManifest.Manifest }.All(manifest => manifest.DependsOn.All(dependency => dependency.Id != ProvidersManifest.Id)),
            "No agent plugin depends on Model providers.");
        Require(new[] { typeof(ClaudeCodeManifest), typeof(CodexManifest), typeof(PiManifest), typeof(OmpManifest) }
            .All(type => type.Assembly.GetReferencedAssemblies().All(reference => reference.Name?.StartsWith("Tereditor.Plugins.Providers", StringComparison.Ordinal) != true)), "No agent assembly references Model providers.");
        var json = PluginJson.Serialize(Routing("codex"));
        Require(PluginJson.Deserialize<AgentRouting>(json) is { Accepts: [{ Auth: [ModelAuthKind.ApiKey] }] } && json.Contains("config-override", StringComparison.Ordinal), "A declaration crosses the wire as JSON.");

        // Matching: kinds intersect; a sign-in counts only when the provider's policy allows it; unknown vendors are not offered.
        ProviderDefinition Bundled(string id) => BundledProviders.All.Single(provider => provider.Id == id);
        Require(ProviderMatching.Offered(Routing("claude-code"), Bundled("anthropic")) && !ProviderMatching.Offered(Routing("claude-code"), Bundled("mistral")), "Claude Code is offered Anthropic, not a provider that only speaks OpenAI Chat.");
        Require(ProviderMatching.Offered(Routing("claude-code"), Bundled("openrouter")) && ProviderMatching.Offered(Routing("claude-code"), Bundled("deepseek")), "Claude Code is offered a router that also speaks the Anthropic API.");
        Require(ProviderMatching.Offered(Routing("codex"), Bundled("openai")) && !ProviderMatching.Offered(Routing("codex"), Bundled("anthropic")) && !ProviderMatching.Offered(Routing("codex"), Bundled("mistral")), "Codex is offered only Responses providers.");
        Require(ProviderMatching.Offered(Routing("pi"), Bundled("mistral")) && ProviderMatching.Offered(Routing("omp"), Bundled("mistral")), "Pi and omp take OpenAI Chat providers.");
        ProviderDefinition WithOAuth(string vendor, bool declared = true) => new("vendor-" + vendor, vendor, ProviderGroup.Other, [new(ModelApiKinds.OpenAiResponses, "https://example.test", [ModelAuthKind.OAuth])]) { OAuth = declared ? ProviderOAuthPolicy.For(vendor) : null };
        Require(ProviderMatching.Offers(Routing("pi"), WithOAuth(ProviderOAuthPolicy.OpenAi)) is [{ Auth: ModelAuthKind.OAuth }], "A sign-in is offered where the vendor allows it and the agent takes it.");
        Require(!ProviderMatching.Offered(Routing("codex"), WithOAuth(ProviderOAuthPolicy.OpenAi)), "An agent that does not take a sign-in for the API is not offered it.");
        Require(!ProviderMatching.Offered(Routing("pi"), WithOAuth(ProviderOAuthPolicy.Anthropic)) && !ProviderMatching.Offered(Routing("pi"), WithOAuth(ProviderOAuthPolicy.Google)), "Anthropic and Google sign-ins are never offered.");
        Require(!ProviderMatching.Offered(Routing("pi"), WithOAuth("some-vendor")) && !ProviderMatching.Offered(Routing("pi"), WithOAuth("some-vendor", declared: false)), "An unknown vendor, or a sign-in with no policy, is not offered.");
        var anthropic = ProviderOAuthPolicy.For(ProviderOAuthPolicy.Anthropic);
        Require(anthropic is { Allowed: false, Overridable: false, ReasonIfNot: { Length: > 0 } } && ProviderOAuthPolicy.For("some-vendor") is { Allowed: false, Overridable: true }, "Anthropic has a fixed reason and no override; an unknown vendor may be marked by the user.");
        Require(Bundled("anthropic").OAuth == anthropic && BundledProviders.All.All(provider => ProviderCatalog.IsProviderId(provider.Id) && provider.Apis.All(api => ModelApiKinds.IsKnown(api.Kind) && Uri.IsWellFormedUriString(api.BaseUrl, UriKind.Absolute))), "Bundled rows are valid data.");

        // The key store: owner-only, atomic, and a key lives nowhere else.
        const string Sentinel = "sk-sentinel-4c1e9b7a2f";
        var state = Path.Combine(root, "providers-store");
        Directory.CreateDirectory(state);
        var keys = new ProviderKeyStore(state);
        List<ProviderDefinition> custom = [];
        List<ModelSourceChoice> sources = [];
        var service = new ModelProvidersService(keys, new ModelProviderState(() => custom, next => custom = [.. next], () => sources, next => sources = [.. next]));
        var report = service.Report();
        Require(report.Providers.Count == BundledProviders.All.Count && report.Providers.All(row => !row.Ready), "No bundled provider is ready before a key is stored.");
        report = service.SaveKey(new("openrouter", Sentinel));
        Require(report.Providers.Single(row => row.Provider.Id == "openrouter").Ready, "A stored key makes the provider ready.");
        var keyFile = Path.Combine(keys.KeysDirectory, "openrouter");
        Require(File.ReadAllText(keyFile) == Sentinel && Directory.GetFiles(keys.KeysDirectory).Length == 1, "The key file holds the key and no temporary file is left.");
        if (!OperatingSystem.IsWindows())
            Require(File.GetUnixFileMode(keyFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite) && File.GetUnixFileMode(keys.KeysDirectory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "The key file is 0600 and its directory 0700.");
        service.SaveKey(new("openrouter", Sentinel + "2"));
        Require(File.ReadAllText(keyFile) == Sentinel + "2", "A key is replaced whole.");
        service.SaveKey(new("openrouter", Sentinel));
        Throws(() => service.SaveKey(new("openrouter", "a\nb")), "A key is one line.");
        Throws(() => service.SaveKey(new("nope", "x")), "A key needs a provider.");
        Throws(() => keys.Save("../x", "x"), "A key's id is a plain provider id.");

        // The helper prints the key and nothing else, and refuses what is not a provider id.
        Require(File.Exists(keys.HelperPath) && ProviderKeyHelper.Words(keys.BinDirectory, "openrouter").SequenceEqual([keys.HelperPath, "openrouter"]), "The helper is written beside the keys, and an agent runs it with the id.");
        if (!OperatingSystem.IsWindows())
        {
            Require(File.GetUnixFileMode(keys.HelperPath).HasFlag(UnixFileMode.UserExecute), "The helper can be run.");
            (int Code, string Out, string Err) Helper(string id)
            {
                var start = new ProcessStartInfo(keys.HelperPath) { RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(id);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (process.ExitCode, output, error);
            }
            Require(Helper("openrouter") == (0, Sentinel, ""), "The helper prints the stored key to standard output.");
            Require(Helper("../keys/openrouter").Code == 2 && Helper(".hidden").Code == 2 && Helper("").Code == 2 && Helper("missing").Code != 0 && Helper("Open Router").Code == 2, "The helper refuses anything but a provider id.");
        }

        // A custom provider: validated, remembered without its key, removed with it.
        Throws(() => service.AddCustom(new("", "https://x.test", [ModelApiKinds.OpenAiChat])), "A custom provider needs a name.");
        Throws(() => service.AddCustom(new("x", "not a url", [ModelApiKinds.OpenAiChat])), "A custom provider needs a URL.");
        Throws(() => service.AddCustom(new("x", "http://example.com/v1", [ModelApiKinds.OpenAiChat])), "Plain http is for this computer only.");
        Throws(() => service.AddCustom(new("x", "https://user:pw@example.com/v1", [ModelApiKinds.OpenAiChat])), "A URL carries no password.");
        Throws(() => service.AddCustom(new("x", "https://example.com/v1", [])), "A custom provider declares an API.");
        Throws(() => service.AddCustom(new("x", "https://example.com/v1", [ModelApiKinds.Vertex])), "A custom provider declares a kind it can speak with a URL and a key.");
        report = service.AddCustom(new("My gateway", "https://llm.example.com/anthropic/", [ModelApiKinds.AnthropicMessages, ModelApiKinds.OpenAiChat]) { Key = Sentinel + "-custom" });
        var added = report.Providers.Single(row => row.Provider.Custom);
        Require(added.Provider is { Id: "custom-my-gateway", NeedsKey: true } && added.Ready && added.Provider.Apis.Select(api => api.Kind).SequenceEqual([ModelApiKinds.AnthropicMessages, ModelApiKinds.OpenAiChat]) && added.Provider.Apis.All(api => api.BaseUrl == "https://llm.example.com/anthropic"), "A custom provider keeps its declared kinds and base URL.");
        Require(ProviderMatching.Offered(Routing("claude-code"), added.Provider) && !ProviderMatching.Offered(Routing("codex"), added.Provider), "A custom provider is offered by its declared kinds.");
        Require(service.AddCustom(new("My gateway", "http://localhost:8080", [ModelApiKinds.OpenAiResponses])).Providers.Any(row => row.Provider.Id == "custom-my-gateway-2" && !row.Provider.NeedsKey && row.Ready), "A name taken gets a new id, and a local provider needs no key.");

        // The recorded source: only a ready provider, cleared when the provider or its key goes.
        Throws(() => service.SetSource(new("claude-code", "mistral")), "A provider with no key cannot be chosen.");
        report = service.SetSource(new("claude-code", "custom-my-gateway"));
        Require(report.Sources is [{ AgentId: "claude-code", ProviderId: "custom-my-gateway" }], "The chosen source is recorded for the agent.");
        Require(service.SetSource(new("claude-code", null)).Sources.Count == 0, "Choosing the agent's own provider clears the record.");
        service.SetSource(new("claude-code", "custom-my-gateway"));
        service.SetSource(new("pi", "openrouter"));
        report = service.RemoveCustom(new("custom-my-gateway"));
        Require(report.Sources is [{ AgentId: "pi" }] && report.Providers.All(row => row.Provider.Id != "custom-my-gateway") && !File.Exists(Path.Combine(keys.KeysDirectory, "custom-my-gateway")), "Removing a custom provider forgets it, its key and the choices that used it.");
        report = service.RemoveKey(new("openrouter"));
        Require(report.Sources.Count == 0 && !File.Exists(keyFile), "Removing a key clears the choices that used it.");
        service.SaveKey(new("openrouter", Sentinel));

        // Contributions: another plugin's providers are merged, validated and withdrawn with the plugin.
        var contributed = new ProviderDefinition("jetbrains-ai", "JetBrains AI", ProviderGroup.JetBrainsAi, [new(ModelApiKinds.AnthropicMessages, "https://jb.test", [ModelAuthKind.ApiKey])]) { NeedsKey = false };
        service.Contribute(new("air-gateway", [contributed, contributed with { Id = "openrouter", Name = "Fake" }]));
        report = service.Report();
        Require(report.Providers.Single(row => row.Provider.Id == "jetbrains-ai") is { Ready: true, Provider.Contributor: "air-gateway" } && report.Providers.Single(row => row.Provider.Id == "openrouter").Provider.Name == "OpenRouter", "A contributed provider joins the list and cannot take over a bundled id.");
        Throws(() => service.Contribute(new("air-gateway", [contributed with { Id = "Bad Id" }])), "A contributed id is validated.");
        service.SetSource(new("claude-code", "jetbrains-ai"));
        Require(service.Withdraw(new("air-gateway")).Ok, "Withdrawing is acknowledged.");
        report = service.Report();
        Require(report.Providers.All(row => row.Provider.Id != "jetbrains-ai") && report.Sources.All(choice => choice.AgentId != "claude-code"), "A withdrawn provider leaves the list and the choices that used it.");

        CodexSeam(service, keys, Sentinel, () => sources);

        // The key is nowhere but its own file: not in a report, saved state, the helper, a launch word, or any other file under the state directory.
        var everything = JsonSerializer.Serialize(service.Report()) + JsonSerializer.Serialize(custom) +
            JsonSerializer.Serialize(sources) + string.Join(' ', ProviderKeyHelper.Words(keys.BinDirectory, "openrouter")) +
            ProviderKeyHelper.Script(keys.KeysDirectory) + ProviderKeyHelper.CmdScript(keys.KeysDirectory);
        Require(!everything.Contains("sentinel", StringComparison.Ordinal), "A key is in no report, saved state, helper or launch word.");
        var holders = Directory.EnumerateFiles(state, "*", SearchOption.AllDirectories).Where(file => File.ReadAllText(file).Contains("sentinel", StringComparison.Ordinal)).Select(file => Path.GetRelativePath(state, file)).ToArray();
        Require(holders.Length == 1 && holders[0] == Path.Combine("providers", "keys", "openrouter"), "Only the key's own file holds it: " + string.Join(", ", holders));
        Require(new AgentLaunchContribution().Environment.Count == 0 && Routing("claude-code").Accepts.All(api => !PluginJson.Serialize(api).Contains("sentinel", StringComparison.Ordinal)), "A declaration or a launch contribution carries no key.");

        // The contract is wire version 3, lists the new methods and pushes the models channel.
        Require(ProvidersContract.WireVersion == 3 && ProvidersContract.Contract.Channels.Any(channel => channel.Name == "models") && new[] { "models", "saveModelKey", "removeModelKey", "addCustomProvider", "removeCustomProvider", "setModelSource", "contribute", "withdraw" }.All(name => ProvidersContract.Contract.Methods.Any(method => method.Name == name)), "The contract lists the model provider methods.");
    }
}