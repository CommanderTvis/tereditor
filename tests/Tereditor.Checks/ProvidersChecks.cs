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

// The Model providers plugin: the one provider list, the key store and its helper, the chosen model source, and the launch seams of Pi
// (models.json in its own agent directory) and omp (models.yml in its tereditor profile), which never carry a key.
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
        Model(root);
        Launch(root);
    }

    private static AgentRouting Routing(string pluginId) => pluginId switch
    {
        "claude-code" => ClaudeCodeManifest.Manifest.AgentRouting!,
        "codex" => CodexManifest.Manifest.AgentRouting!,
        "pi" => PiManifest.Manifest.AgentRouting!,
        _ => OmpManifest.Manifest.AgentRouting!
    };

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

        // The key is nowhere but its own file: not in a report, saved state, the helper, a launch word, or any other file under the state directory.
        var everything = JsonSerializer.Serialize(service.Report()) + JsonSerializer.Serialize(custom) +
            JsonSerializer.Serialize(sources) + string.Join(' ', ProviderKeyHelper.Words(keys.BinDirectory, "openrouter")) +
            ProviderKeyHelper.Script(keys.KeysDirectory) + ProviderKeyHelper.CmdScript(keys.KeysDirectory);
        Require(!everything.Contains("sentinel", StringComparison.Ordinal), "A key is in no report, saved state, helper or launch word.");
        var holders = Directory.EnumerateFiles(state, "*", SearchOption.AllDirectories).Where(file => File.ReadAllText(file).Contains("sentinel", StringComparison.Ordinal)).Select(file => Path.GetRelativePath(state, file)).ToArray();
        Require(holders.Length == 1 && holders[0] == Path.Combine("providers", "keys", "openrouter"), "Only the key's own file holds it: " + string.Join(", ", holders));
        Require(new AgentLaunchContribution().Environment.Count == 0 && Routing("claude-code").Accepts.All(api => !PluginJson.Serialize(api).Contains("sentinel", StringComparison.Ordinal)), "A declaration or a launch contribution carries no key.");

        // The contract is wire version 3: the model provider methods only, no per-agent key-file methods.
        Require(ProvidersContract.WireVersion == 3 && new[] { "list", "setKey", "removeKey" }.All(name => ProvidersContract.Contract.Methods.All(method => method.Name != name)) && new[] { "models", "saveModelKey", "removeModelKey", "addCustomProvider", "removeCustomProvider", "setModelSource", "contribute", "withdraw" }.All(name => ProvidersContract.Contract.Methods.Any(method => method.Name == name)), "The contract lists the model provider methods.");
    }

    // The launch seams: a chosen source writes the agent's models file with a helper command and no key, and the launch gets only the place to read it.
    private static string Flat(AgentLaunchContribution c) => string.Join(" ", c.Wrapper.Concat(c.Arguments).Concat(c.Environment.Select(pair => pair.Key + "=" + pair.Value)));

    private static void Launch(string root)
    {
        const string Sentinel = "sk-sentinel-77d0e3b1c9";
        var state = Path.Combine(root, "providers-launch");
        var ompHome = Path.Combine(root, "providers-omp-home");
        Directory.CreateDirectory(state);
        var keys = new ProviderKeyStore(state);
        var files = new AgentModelFiles(keys, state, ompHome);
        List<ProviderDefinition> custom = [];
        List<ModelSourceChoice> sources = [];
        ModelProvidersService Make() => new(keys, new ModelProviderState(() => custom, next => custom = [.. next], () => sources, next => sources = [.. next]), files);
        var service = Make();
        Require(service.Launch("pi") is null && service.Launch("omp") is null, "With no source chosen no launch is touched.");
        Require(new ModelProvidersService(keys, new ModelProviderState(() => custom, next => custom = [.. next], () => sources, next => sources = [.. next])).Launch("pi") is null, "Without a models file service nothing contributes.");

        service.SaveKey(new("openrouter", Sentinel));
        service.SetSource(new("pi", "openrouter"));
        service.SetSource(new("omp", "openrouter"));
        var piFile = files.PathOf("pi");
        var ompFile = files.PathOf("omp");
        var pi = JsonDocument.Parse(File.ReadAllText(piFile)).RootElement.GetProperty("providers").GetProperty("openrouter");
        var command = pi.GetProperty("apiKey").GetString()!;
        Require(command.StartsWith('!') && command.Contains(keys.HelperPath, StringComparison.Ordinal) && command.Contains("openrouter", StringComparison.Ordinal) && pi.GetProperty("baseUrl").GetString() == "https://openrouter.ai/api/v1" && !pi.TryGetProperty("api", out _),
            "Pi's file names the endpoint and gets the key from the helper; a provider Pi knows keeps its own API.");
        Require(Directory.GetFiles(files.PiAgentDirectory).Select(Path.GetFileName).SequenceEqual(["models.json"]), "Pi's own agent directory holds the models file and never an auth.json.");
        var yaml = File.ReadAllText(ompFile);
        Require(ompFile == Path.Combine(ompHome, ".omp", "profiles", "tereditor", "agent", "models.yml") && yaml.Contains("providers:\n  \"openrouter\":", StringComparison.Ordinal) && yaml.Contains("apiKey: \"!", StringComparison.Ordinal) && yaml.Contains(keys.HelperPath, StringComparison.Ordinal),
            "omp's file is in its tereditor profile and gets the key from the helper.");
        Require(!File.ReadAllText(piFile).Contains("sentinel", StringComparison.Ordinal) && !yaml.Contains("sentinel", StringComparison.Ordinal), "No models file holds the key.");
        if (!OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command[1..]);
            using var run = Process.Start(start)!;
            var printed = run.StandardOutput.ReadToEnd();
            run.WaitForExit();
            Require(printed == Sentinel, "The command in Pi's file prints the key when a shell runs it.");
        }

        var piLaunch = service.Launch("pi")!;
        var ompLaunch = service.Launch("omp")!;
        Require(piLaunch.Environment is { Count: 1 } environment && environment["PI_CODING_AGENT_DIR"] == files.PiAgentDirectory && piLaunch.Arguments.Count == 0 && piLaunch.Wrapper.Count == 0, "Pi's launch gets only its agent directory.");
        Require(ompLaunch.Arguments.SequenceEqual(["--profile", "tereditor"]) && ompLaunch.Environment.Count == 0 && service.Launch("claude-code") is null && service.Launch("codex") is null, "omp's launch gets only its profile, and the other agents get nothing.");
        Require(!Flat(piLaunch).Contains("sentinel", StringComparison.Ordinal) && !Flat(ompLaunch).Contains("sentinel", StringComparison.Ordinal), "A launch contribution carries no key.");

        // The omp launch line: byte-identical without a source, one profile flag with it.
        Require(OmpLaunch.Line("omp", new() { Mcp = true }) == "omp --profile tereditor --no-lsp" && OmpLaunch.Line("omp", new() { Mcp = false }) == "omp --no-lsp", "Without a source the omp line is the one omp builds itself.");
        Require(OmpLaunch.Line("omp", new() { Mcp = false, Contributions = [ompLaunch] }) == "omp --profile tereditor --no-lsp" && OmpLaunch.Line("omp", new() { Mcp = true, Contributions = [ompLaunch] }) == "omp --profile tereditor --no-lsp",
            "A model source starts omp in the tereditor profile once, with or without MCP.");

        Require(OmpLaunch.Line("/opt/wrap/omp-wrapper", new() { Mcp = false, Contributions = [ompLaunch] }) == "/opt/wrap/omp-wrapper --profile tereditor --no-lsp", "A custom command still starts in the profile.");
        var seen = new ModelSourceLaunch();
        Require(seen.For("omp") is null, "The UI contributes nothing before a source is known.");
        seen.Update(new([], [new("omp", "openrouter"), new("claude-code", "openrouter")]));
        Require(seen.For("omp")!.Arguments.SequenceEqual(["--profile", "tereditor"]) && seen.For("pi") is null && seen.For("claude-code") is null, "The UI contributes omp's profile only while a source is chosen for omp.");
        seen.Update(new([], []));
        Require(seen.For("omp") is null, "The UI contribution goes with the source.");

        // A provider an agent knows by id keeps its own API; Pi refuses one it does not know (it needs a list of models), omp discovers the models.
        var knownPi = JsonDocument.Parse(AgentModelFiles.PiJson(BundledProviders.All.Single(provider => provider.Id == "openrouter") with { NeedsKey = false }, BundledProviders.All.Single(provider => provider.Id == "openrouter").Apis[0], "unused", known: true))
            .RootElement.GetProperty("providers").GetProperty("openrouter");
        Require(knownPi.GetProperty("apiKey").GetString() == "none", "A provider that needs no key is written without a helper.");
        service.SaveKey(new("deepseek", Sentinel));
        service.SaveKey(new("together", Sentinel));
        service.SetSource(new("pi", "deepseek"));
        Require(JsonDocument.Parse(File.ReadAllText(piFile)).RootElement.GetProperty("providers").GetProperty("deepseek").TryGetProperty("api", out _) == false, "Pi knows DeepSeek by id.");
        Throws(() => service.SetSource(new("pi", "together")), "Pi is not given a provider it does not know.");
        service.SetSource(new("omp", "together"));
        Require(!File.ReadAllText(ompFile).Contains("discovery", StringComparison.Ordinal), "omp knows Together by id.");
        service.AddCustom(new("My gateway", "https://llm.example.com/v1", [ModelApiKinds.AnthropicMessages, ModelApiKinds.OpenAiChat]) { Key = Sentinel + "-custom" });
        Throws(() => service.SetSource(new("pi", "custom-my-gateway")), "Pi is not given a custom provider.");
        Require(sources.Single(choice => choice.AgentId == "pi").ProviderId == "deepseek", "A source that could not be written is not recorded.");
        service.SetSource(new("omp", "custom-my-gateway"));
        yaml = File.ReadAllText(ompFile);
        Require(yaml.Contains("api: openai-completions", StringComparison.Ordinal) && yaml.Contains("type: openai-models-list", StringComparison.Ordinal) && yaml.Contains("\"https://llm.example.com/v1\"", StringComparison.Ordinal),
            "omp is given a custom provider through its OpenAI API, with its models discovered.");
        service.AddCustom(new("Claude only", "https://claude.example.com", [ModelApiKinds.AnthropicMessages]) { Key = Sentinel });
        Throws(() => service.SetSource(new("omp", "custom-claude-only")), "omp is not given an unknown provider that has no OpenAI API.");
        service.AddCustom(new("Local", "http://localhost:8080", [ModelApiKinds.OpenAiChat]));
        service.SetSource(new("omp", "custom-local"));
        Require(File.ReadAllText(ompFile).Contains("auth: none", StringComparison.Ordinal), "A provider that needs no key is written without a helper.");
        Throws(() => service.SetSource(new("omp", "jetbrains-ai")), "A provider that is not in the list cannot be chosen.");

        // Startup rewrites the files for the sources already chosen; a lost source or key removes the file and the contribution.
        File.Delete(piFile);
        File.Delete(ompFile);
        var restarted = Make();
        Require(restarted.Launch("pi") is null, "Before startup syncs, nothing contributes.");
        restarted.Sync();
        Require(File.Exists(piFile) && File.Exists(ompFile) && restarted.Launch("pi") is not null && restarted.Launch("omp") is not null, "Startup writes the files again and contributes.");
        var report = restarted.SetSource(new("pi", null));
        Require(!File.Exists(piFile) && restarted.Launch("pi") is null && restarted.Launch("omp") is not null && report.Sources.All(choice => choice.AgentId != "pi"), "Choosing the agent's own provider removes the file and the contribution.");
        sources = [.. sources.Where(choice => choice.AgentId != "pi"), new("pi", "custom-local")];
        File.Delete(piFile);
        Make().Sync();
        Require(sources.All(choice => choice.AgentId != "pi") && !File.Exists(piFile), "Startup drops a source whose file cannot be written, so recorded sources match what launches use.");
        restarted = Make();
        restarted.Sync();
        restarted.RemoveCustom(new("custom-local"));
        Require(!File.Exists(ompFile) && restarted.Launch("omp") is null, "Removing the provider an agent used removes its file and its contribution.");
        Require(!PluginJson.Serialize(restarted.Report()).Contains("sentinel", StringComparison.Ordinal), "A report carries no key.");
    }
}
