using System.Diagnostics;
using System.Text.Json;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.ClaudeCode;
using Tereditor.Plugins.ClaudeCode.UI;
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
        Links();
        Model(root);
        Launch(root);
    }

    // Every bundled provider links to its site; every address is plain https, with no query, fragment or tracking part, and no credentials.
    private static void Links()
    {
        foreach (var provider in BundledProviders.All)
        {
            Require(provider.Links is { Website.Length: > 0 }, provider.Id + " has a Website link.");
            foreach (var url in new[] { provider.Links!.Website, provider.Links.KeyUrl, provider.Links.Docs }.OfType<string>())
                Require(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0 &&
                    !url.Contains("utm_", StringComparison.OrdinalIgnoreCase) && !url.Contains("ref=", StringComparison.OrdinalIgnoreCase), provider.Id + " link " + url + " is plain https with no tracking.");
        }
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
        Require(!line.Contains(sentinel, StringComparison.Ordinal) && !words.Contains(sentinel, StringComparison.Ordinal) && !JsonSerializer.Serialize(report).Contains(sentinel, StringComparison.Ordinal), "The key is in no launch word and no report.");
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
        var local = report.Providers.Single(row => row.Provider.Name == "It's local");
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

    // The Claude Code seam: a chosen source writes a settings file with the helper command and the endpoint, and the launch gets only --settings.
    private static void ClaudeSeam(ModelProvidersService service, ProviderKeyStore keys, AgentModelFiles files, string sentinel, Func<List<ModelSourceChoice>> sources)
    {
        var file = files.ClaudeSettingsPath;
        Require(!File.Exists(file) && ProviderLaunch.For("claude", service.Report()) is null && ProviderLaunch.For("claude", null) is null, "With no source chosen Claude Code is untouched.");
        var report = service.SetSource(new("claude-code", "openrouter"));
        var text = File.ReadAllText(file);
        var settings = JsonDocument.Parse(text).RootElement;
        var helper = settings.GetProperty("apiKeyHelper").GetString()!;
        Require(helper.Contains(keys.HelperPath, StringComparison.Ordinal) && helper.EndsWith("openrouter'", StringComparison.Ordinal) || OperatingSystem.IsWindows() && helper.EndsWith("openrouter\"", StringComparison.Ordinal), "The settings file runs the key helper for the provider.");
        Require(settings.GetProperty("env").GetProperty("ANTHROPIC_BASE_URL").GetString() == "https://openrouter.ai/api" && !text.Contains(sentinel, StringComparison.Ordinal), "The settings file names the Anthropic endpoint and holds no key.");
        if (!OperatingSystem.IsWindows())
        {
            Require((File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "The settings file is owner-only.");
            using var shell = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", helper]) { RedirectStandardOutput = true, UseShellExecute = false })!;
            var printed = shell.StandardOutput.ReadToEnd().Trim();
            shell.WaitForExit();
            Require(printed == sentinel, "The apiKeyHelper command prints the key when a shell runs it.");
        }
        var contribution = ProviderLaunch.For("claude", report) ?? throw new InvalidOperationException("providers: a chosen source contributes to the Claude Code launch.");
        Require(contribution.BlockedReason is null && contribution.Arguments.SequenceEqual(["--settings", file]) && contribution.Wrapper.Count == 0 && contribution.Environment.Count == 0 && ProviderLaunch.ClaudeSettings(report.Helper) == file,
            "The launch gets only --settings with the generated file, which both halves derive from the helper path.");
        Require(!Flat(contribution).Contains(sentinel, StringComparison.Ordinal) && !JsonSerializer.Serialize(report).Contains(sentinel, StringComparison.Ordinal), "The key is in no launch word and no report.");
        Require(new[] { "codex", "pi", "omp", "other" }.All(launcher => ProviderLaunch.For(launcher, report) is null), "Only Claude Code launches change.");
        // The user's own variables and arguments stay; their own --settings blocks the launch, because two may not merge.
        var line = ClaudeLaunch.SessionCommand("A=1 claude --model sonnet", "--model opus", false, false, contributions: [contribution]);
        Require(line.Contains("A=1 claude --settings " + file + " --model sonnet", StringComparison.Ordinal) && line.Contains("--model opus", StringComparison.Ordinal), "The terminal line keeps the user's variable and arguments after the generated file: " + line);
        foreach (var own in new[] { "A=1 claude --settings /mine.json", "claude --settings=/mine.json", "claude --settings '/my file.json'" })
        {
            var conflict = ClaudeLaunch.SessionCommand(own, "", false, false, contributions: [contribution]);
            Require(conflict.Contains("launcher unavailable: The Claude command has its own --settings, so the model source cannot apply. Remove it or clear the source.", StringComparison.Ordinal) && !conflict.Contains("--settings " + file, StringComparison.Ordinal), "A --settings of the user's own blocks the launch: " + conflict);
        }
        Require(AgentLaunchComposition.Conflict([contribution], ["--model", "x"]) is null && AgentLaunchComposition.Conflict([contribution], ["--settings", "x"]) is { Length: > 0 }, "The chat's words are checked the same way.");
        var seen = new ModelSourceLaunch();
        seen.Update(report);
        Require(seen.For("claude") is null, "The UI half's omp contribution does not answer for Claude Code.");

        // A provider with no Anthropic API is not offered and cannot be chosen; a keyless one gets a placeholder and no helper; an unusable source blocks.
        Throws(() => service.SetSource(new("claude-code", "mistral")), "A provider without an Anthropic API cannot be Claude Code's source.");
        Require(sources().Single(choice => choice.AgentId == "claude-code").ProviderId == "openrouter", "A refused choice leaves the recorded source as it was.");
        var local = service.AddCustom(new("Claude local", "http://localhost:4000", [ModelApiKinds.AnthropicMessages])).Providers.Single(row => row.Provider.Name == "Claude local");
        report = service.SetSource(new("claude-code", local.Provider.Id));
        var keyless = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        Require(!keyless.TryGetProperty("apiKeyHelper", out _) && keyless.GetProperty("env").GetProperty("ANTHROPIC_AUTH_TOKEN").GetString() == "none" && keyless.GetProperty("env").GetProperty("ANTHROPIC_BASE_URL").GetString() == "http://localhost:4000",
            "A provider that needs no key is written without a helper.");
        var notReady = new ModelProvidersReport([local with { Ready = false }], [new("claude-code", local.Provider.Id)]) { Helper = keys.HelperPath };
        var noApi = new ModelProvidersReport([new(BundledProviders.All.Single(provider => provider.Id == "mistral"), true)], [new("claude-code", "mistral")]) { Helper = keys.HelperPath };
        var noHelper = new ModelProvidersReport(report.Providers, [new("claude-code", local.Provider.Id)]);
        Require(new[] { notReady, noApi, noHelper }.All(blocked => ProviderLaunch.For("claude", blocked)?.BlockedReason is { Length: > 0 }), "A source Claude Code cannot use blocks the launch.");
        Require(ClaudeLaunch.SessionCommand("claude", "", false, false, contributions: [ProviderLaunch.For("claude", notReady)!]).Contains("launcher unavailable", StringComparison.Ordinal), "A blocked Claude Code line prints the reason and exits.");

        // Clearing the source, removing the provider and startup all keep the file in step.
        service.SetSource(new("claude-code", null));
        Require(!File.Exists(file) && ProviderLaunch.For("claude", service.Report()) is null, "Clearing the source removes the file and the launch change.");
        service.SetSource(new("claude-code", "openrouter"));
        File.Delete(file);
        service.Sync();
        Require(File.Exists(file), "Startup writes the file again.");
        service.SetSource(new("claude-code", local.Provider.Id));
        service.RemoveCustom(new(local.Provider.Id));
        Require(!File.Exists(file) && sources().All(choice => choice.AgentId != "claude-code"), "Removing the provider drops the source and the file.");
    }

    // The model source work: what each agent declares, which providers it is offered, the key store and its helper, custom providers and the recorded source.
    private static void Model(string root)
    {
        // Declarations: each agent says what it accepts and none depends on Model providers.
        Require(Routing("claude-code").Accepts is [{ Kind: ModelApiKinds.AnthropicMessages, Injection: AgentInjection.SettingsFile }, { Kind: ModelApiKinds.Bedrock, Injection: AgentInjection.Environment, Auth: [ModelAuthKind.Platform] }, { Kind: ModelApiKinds.Vertex, Injection: AgentInjection.Environment, Auth: [ModelAuthKind.Platform] }],
            "Claude Code accepts the Anthropic Messages API through a settings file, and Bedrock and Vertex with the platform's credentials through the environment.");
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
        Require(new[] { "anthropic", "openrouter", "deepseek", "xai" }.All(id => ProviderMatching.Offered(Routing("claude-code"), Bundled(id))) && !ProviderMatching.Offered(Routing("claude-code"), Bundled("mistral")) && !ProviderMatching.Offered(Routing("claude-code"), Bundled("openai")),
            "Claude Code is offered only providers with an Anthropic-compatible API.");
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
        // One address per API: DeepSeek-like, OpenAI-style at the root and Anthropic under /anthropic. An unlisted API takes the main address.
        Throws(() => service.AddCustom(new("x", "https://example.com", [ModelApiKinds.OpenAiChat]) { Urls = [new(ModelApiKinds.AnthropicMessages, "https://example.com/anthropic")] }), "An address for an API that is not chosen is refused.");
        Throws(() => service.AddCustom(new("x", "https://example.com", [ModelApiKinds.AnthropicMessages]) { Urls = [new(ModelApiKinds.AnthropicMessages, "http://example.com/anthropic")] }), "An API address follows the same rules as the main one.");
        var split = service.AddCustom(new("Split host", "https://api.split.test/", [ModelApiKinds.OpenAiChat, ModelApiKinds.OpenAiResponses, ModelApiKinds.AnthropicMessages]) { Urls = [new(ModelApiKinds.AnthropicMessages, "https://api.split.test/anthropic/")] }).Providers.Single(row => row.Provider.Id == "custom-split-host");
        Require(split.Provider.Apis.ToDictionary(api => api.Kind, api => api.BaseUrl) is { Count: 3 } urls && urls[ModelApiKinds.OpenAiChat] == "https://api.split.test" && urls[ModelApiKinds.OpenAiResponses] == "https://api.split.test" && urls[ModelApiKinds.AnthropicMessages] == "https://api.split.test/anthropic", "Each API keeps its own address; one without an entry takes the main one.");
        Throws(() => service.SetCustomApiUrl(new("custom-split-host", ModelApiKinds.GoogleGenerative, "https://api.split.test/g")), "An API the provider lacks cannot be changed.");
        Throws(() => service.SetCustomApiUrl(new("openrouter", ModelApiKinds.OpenAiChat, "https://x.test")), "A bundled provider's address cannot be changed.");
        Throws(() => service.SetCustomApiUrl(new("custom-split-host", ModelApiKinds.OpenAiChat, "ftp://x.test")), "A changed address is validated.");
        service.SaveKey(new("custom-split-host", Sentinel));
        Require(service.SetCustomApiUrl(new("custom-split-host", ModelApiKinds.OpenAiResponses, "https://api.split.test/v1/")).Providers.Single(row => row.Provider.Id == "custom-split-host").Provider.Apis.Select(api => api.BaseUrl).SequenceEqual(["https://api.split.test", "https://api.split.test/v1", "https://api.split.test/anthropic"]), "Changing one API's address leaves the others.");
        var splitReport = service.Report();
        Require(ProviderLaunch.For("codex", splitReport with { Sources = [new("codex", "custom-split-host")] })?.Arguments?.Any(word => word.Contains("base_url='https://api.split.test/v1'", StringComparison.Ordinal)) == true, "Codex's Responses address is the Responses API's own.");
        Require(ProviderLaunch.ClaudeApi(splitReport.Providers.Single(row => row.Provider.Id == "custom-split-host").Provider)?.BaseUrl == "https://api.split.test/anthropic", "Claude Code's address is the Anthropic API's own.");
        service.RemoveCustom(new("custom-split-host"));
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

        // The contract is wire version 5, lists the model provider methods only (no per-agent key-file methods) and pushes the models channel.
        Require(ProvidersContract.WireVersion == 5 && ProvidersContract.Contract.Channels.Any(channel => channel.Name == "models") && new[] { "list", "setKey", "removeKey" }.All(name => ProvidersContract.Contract.Methods.All(method => method.Name != name)) && new[] { "models", "saveModelKey", "removeModelKey", "addCustomProvider", "removeCustomProvider", "setCustomApiUrl", "setModelSource", "refreshSignIn", "contribute", "withdraw" }.All(name => ProvidersContract.Contract.Methods.Any(method => method.Name == name)), "The contract lists the model provider methods.");
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

        ClaudeSeam(service, keys, files, Sentinel, () => sources);

        // A provider an agent knows by id keeps its own API; Pi refuses one it does not know (it needs a list of models), omp discovers the models.
        var knownPi = JsonDocument.Parse(AgentModelFiles.PiJson(BundledProviders.All.Single(provider => provider.Id == "openrouter") with { NeedsKey = false }, BundledProviders.All.Single(provider => provider.Id == "openrouter").Apis[0], "unused", known: true, []))
            .RootElement.GetProperty("providers").GetProperty("openrouter");
        Require(knownPi.GetProperty("apiKey").GetString() == "none", "A provider that needs no key is written without a helper.");
        service.SaveKey(new("deepseek", Sentinel));
        service.SaveKey(new("together", Sentinel));
        service.SetSource(new("pi", "deepseek"));
        Require(JsonDocument.Parse(File.ReadAllText(piFile)).RootElement.GetProperty("providers").GetProperty("deepseek").TryGetProperty("api", out _) == false, "Pi knows DeepSeek by id.");
        service.SetSource(new("pi", "together") { Model = "m-1" });
        var generic = JsonDocument.Parse(File.ReadAllText(piFile)).RootElement.GetProperty("providers").GetProperty("together");
        Require(generic.GetProperty("api").GetString() == "openai-completions" && generic.GetProperty("models")[0].GetProperty("id").GetString() == "m-1" && generic.GetProperty("apiKey").GetString()!.StartsWith('!'), "Pi gets a provider it does not know as a generic OpenAI entry with the selected model and the key helper.");
        service.SetSource(new("pi", "deepseek"));
        File.WriteAllText(ompFile, "providers: {}\n");
        service.SetSource(new("omp", null));
        Require(File.Exists(ompFile), "Clearing an omp source leaves a models.yml tereditor did not write.");
        service.SetSource(new("omp", "together"));
        Require(!File.ReadAllText(ompFile).Contains("discovery", StringComparison.Ordinal), "omp knows Together by id.");
        service.AddCustom(new("My gateway", "https://llm.example.com/v1", [ModelApiKinds.AnthropicMessages, ModelApiKinds.OpenAiChat]) { Key = Sentinel + "-custom" });
        service.SetSource(new("pi", "custom-my-gateway"));
        Require(JsonDocument.Parse(File.ReadAllText(piFile)).RootElement.GetProperty("providers").GetProperty("custom-my-gateway").GetProperty("models")[0].GetProperty("id").GetString() == "default", "A custom provider with no model named lists the one model default.");
        service.SetSource(new("pi", "deepseek"));
        service.SetSource(new("omp", "custom-my-gateway"));
        yaml = File.ReadAllText(ompFile);
        Require(yaml.Contains("api: openai-completions", StringComparison.Ordinal) && yaml.Contains("- id: \"default\"", StringComparison.Ordinal) && yaml.Contains("\"https://llm.example.com/v1\"", StringComparison.Ordinal),
            "omp is given a custom provider through its OpenAI API with a model list.");
        service.AddCustom(new("Claude only", "https://claude.example.com", [ModelApiKinds.AnthropicMessages]) { Key = Sentinel });
        service.SetSource(new("omp", "custom-claude-only"));
        Require(File.ReadAllText(ompFile).Contains("api: anthropic-messages", StringComparison.Ordinal), "A provider with only an Anthropic API is written with that adapter.");
        // One provider, one address per API: each agent's file carries the address of the API it uses.
        service.AddCustom(new("Two hosts", "https://api.two.test", [ModelApiKinds.OpenAiChat, ModelApiKinds.AnthropicMessages]) { Key = Sentinel, Urls = [new(ModelApiKinds.AnthropicMessages, "https://api.two.test/anthropic")] });
        service.SetSource(new("pi", "custom-two-hosts"));
        service.SetSource(new("omp", "custom-two-hosts"));
        service.SetSource(new("claude-code", "custom-two-hosts"));
        var claudeFile = files.ClaudeSettingsPath;
        Require(JsonDocument.Parse(File.ReadAllText(piFile)).RootElement.GetProperty("providers").GetProperty("custom-two-hosts").GetProperty("baseUrl").GetString() == "https://api.two.test"
            && File.ReadAllText(ompFile).Contains("\"https://api.two.test\"", StringComparison.Ordinal) && !File.ReadAllText(ompFile).Contains("/anthropic", StringComparison.Ordinal)
            && JsonDocument.Parse(File.ReadAllText(claudeFile)).RootElement.GetProperty("env").GetProperty("ANTHROPIC_BASE_URL").GetString() == "https://api.two.test/anthropic", "Pi and omp get the OpenAI address and Claude Code the Anthropic one.");
        service.SetCustomApiUrl(new("custom-two-hosts", ModelApiKinds.AnthropicMessages, "https://api.two.test/claude"));
        service.SetCustomApiUrl(new("custom-two-hosts", ModelApiKinds.OpenAiChat, "https://api.two.test/v1"));
        Require(JsonDocument.Parse(File.ReadAllText(piFile)).RootElement.GetProperty("providers").GetProperty("custom-two-hosts").GetProperty("baseUrl").GetString() == "https://api.two.test/v1"
            && File.ReadAllText(ompFile).Contains("\"https://api.two.test/v1\"", StringComparison.Ordinal)
            && JsonDocument.Parse(File.ReadAllText(claudeFile)).RootElement.GetProperty("env").GetProperty("ANTHROPIC_BASE_URL").GetString() == "https://api.two.test/claude", "Changing an address rewrites the files of the agents that use the provider.");
        service.SetSource(new("claude-code", null));
        service.SetSource(new("pi", "deepseek"));
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
        sources = [.. sources.Where(choice => choice.AgentId != "pi"), new("pi", "no-such-provider")];
        File.Delete(piFile);
        Make().Sync();
        Require(sources.All(choice => choice.AgentId != "pi") && !File.Exists(piFile), "Startup drops a source that can no longer be written, so recorded sources match what launches use.");
        restarted = Make();
        restarted.Sync();
        restarted.RemoveCustom(new("custom-local"));
        Require(!File.Exists(ompFile) && restarted.Launch("omp") is null, "Removing the provider an agent used removes its file and its contribution.");
        Require(!PluginJson.Serialize(restarted.Report()).Contains("sentinel", StringComparison.Ordinal), "A report carries no key.");

        // A provider its contributor wires to agents itself (JetBrains (Air Gateway)): no file, no launch word of ours, offered to those agents only.
        var wired = new ProviderDefinition("jetbrains-ai", "JetBrains (Air Gateway)", ProviderGroup.JetBrainsAi, []) { NeedsKey = false, WiredAgents = ["claude-code", "codex", "pi"] };
        restarted.Contribute(new("air-gateway", [wired]));
        restarted.SetSource(new("pi", "jetbrains-ai"));
        restarted.SetSource(new("claude-code", "jetbrains-ai"));
        restarted.SetSource(new("codex", "jetbrains-ai"));
        var wiredReport = restarted.Report();
        Require(!File.Exists(piFile) && restarted.Launch("pi") is null && sources.Count(choice => choice.ProviderId == "jetbrains-ai") == 3, "A wired provider writes no models file and adds no launch of its own for Pi.");
        Require(ProviderLaunch.For("claude", wiredReport, false) is null && ProviderLaunch.For("codex", wiredReport) is null, "Model providers adds nothing to a Claude Code or Codex launch the contributor wires: no block, no settings file.");
        Throws(() => restarted.SetSource(new("omp", "jetbrains-ai")), "An agent the contributor does not wire cannot take it.");
        Make().Sync();
        Require(sources.Count(choice => choice.ProviderId == "jetbrains-ai") == 3, "A start before the contributor is back keeps its choices, Pi's included.");
        restarted.Withdraw(new("air-gateway"));
        Require(sources.All(choice => choice.ProviderId != "jetbrains-ai"), "When the contributor stops its choices go, so the agents are back on their own providers.");
    }
}