using Tereditor.Plugins.Api;
using Tereditor.Plugins.Bedrock;
using Tereditor.Plugins.ClaudeCode;
using Tereditor.Plugins.Codex;
using Tereditor.Plugins.Omp;
using Tereditor.Plugins.Pi;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.Providers.Host;
using Tereditor.Plugins.Vertex;

namespace Tereditor.Checks;

// The Cloud platforms plugins (Amazon Bedrock, Google Vertex AI): manifests, the provider each contributes from its non-secret settings, who is offered it
// (Claude Code only), and the Claude Code launch seam, which sets variables and never carries a secret.
internal static class CloudPlatformChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("cloud-platforms: " + message);
    }

    internal static void Run(string root)
    {
        foreach (var (manifest, icon) in new[] { (BedrockManifest.Manifest, "bedrock.svg"), (VertexManifest.Manifest, "vertex.svg") })
        {
            Require(manifest is { Category: PluginCategories.ModelProviders, Headless: HeadlessSupport.No, Assets: "assets" } && manifest.Host is not null && manifest.Ui is not null
                && manifest.DependsOn.Single() is { Id: "providers" } dependency && dependency.WireVersion == ProvidersContract.WireVersion && manifest.Icon == "asset:" + icon,
                manifest.Id + ": a Model providers plugin with both halves, depending on Model providers at its wire version, with its icon");
            Require(manifest.AgentRouting is null, manifest.Id + ": a platform plugin declares no agent routing; the agents declare what they accept");
        }

        // Nothing is offered until the settings are complete and safe: a value that could reach a shell is refused.
        Require(BedrockPlatform.Definition(new()) is null && VertexPlatform.Definition(new()) is null, "an unconfigured platform contributes nothing");
        Require(BedrockPlatform.Definition(new() { Region = "us-east-1; rm -rf /" }) is null && BedrockPlatform.Definition(new() { Region = "us-east-1", Profile = "a b" }) is null
            && VertexPlatform.Definition(new() { Project = "my-project-123", Region = "$(x)" }) is null && VertexPlatform.Definition(new() { Project = "No Spaces" }) is null, "an unsafe value is refused");
        var bedrock = BedrockPlatform.Definition(new() { Region = "eu-west-1", Profile = "work" }) ?? throw new InvalidOperationException("cloud-platforms: complete Bedrock settings contribute a provider");
        var vertex = VertexPlatform.Definition(new() { Project = "my-project-123", Region = "us-east5" }) ?? throw new InvalidOperationException("cloud-platforms: complete Vertex settings contribute a provider");
        Require(bedrock is { Id: "bedrock", Group: ProviderGroup.Hyperscalers, NeedsKey: false } && bedrock.Apis is [{ Kind: ModelApiKinds.Bedrock, Auth: [ModelAuthKind.Platform], BaseUrl: "https://bedrock-runtime.eu-west-1.amazonaws.com" }], "Bedrock is a keyless Cloud platforms provider on the Bedrock API");
        Require(bedrock.Environment.OrderBy(pair => pair.Key).SequenceEqual([new("AWS_PROFILE", "work"), new("AWS_REGION", "eu-west-1"), new("CLAUDE_CODE_USE_BEDROCK", "1")]), "Bedrock sets the Claude Code switch, the region and the profile");
        Require(BedrockPlatform.Definition(new() { Region = "eu-west-1" })!.Environment.ContainsKey("AWS_PROFILE") == false, "an empty profile sets no profile");
        Require(vertex is { Id: "vertex", Group: ProviderGroup.Hyperscalers, NeedsKey: false } && vertex.Apis is [{ Kind: ModelApiKinds.Vertex, Auth: [ModelAuthKind.Platform], BaseUrl: "https://us-east5-aiplatform.googleapis.com" }], "Vertex is a keyless Cloud platforms provider on the Vertex API");
        Require(vertex.Environment.OrderBy(pair => pair.Key).SequenceEqual([new("ANTHROPIC_VERTEX_PROJECT_ID", "my-project-123"), new("CLAUDE_CODE_USE_VERTEX", "1"), new("CLOUD_ML_REGION", "us-east5")]), "Vertex sets the Claude Code switch, the project and the region");
        Require(VertexPlatform.Definition(new() { Project = "my-project-123" })!.Apis[0].BaseUrl == "https://aiplatform.googleapis.com", "the global Vertex region has the global endpoint");
        Require(new[] { bedrock, vertex }.All(provider => !provider.Environment.Keys.Any(name => name.Contains("KEY", StringComparison.OrdinalIgnoreCase) || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase))), "no secret variable is set");

        // Who is offered what: only Claude Code declares these platforms; the others are offered nothing.
        static AgentRouting Routing(string id) => id switch { "claude-code" => ClaudeCodeManifest.Manifest.AgentRouting!, "codex" => CodexManifest.Manifest.AgentRouting!, "pi" => PiManifest.Manifest.AgentRouting!, _ => OmpManifest.Manifest.AgentRouting! };
        Require(ProviderMatching.Offered(Routing("claude-code"), bedrock) && ProviderMatching.Offered(Routing("claude-code"), vertex), "Claude Code is offered both platforms");
        Require(new[] { "codex", "pi", "omp" }.All(agent => !ProviderMatching.Offered(Routing(agent), bedrock) && !ProviderMatching.Offered(Routing(agent), vertex)), "Codex, Pi and omp are offered neither");
        Require(ProviderMatching.Offers(Routing("claude-code"), bedrock) is [{ Auth: ModelAuthKind.Platform, Agent.Injection: AgentInjection.Environment }], "the offer is the platform's own credentials, set through the environment");
        Require(!ProviderMatching.Offered(Routing("claude-code"), bedrock with { Apis = [new(ModelApiKinds.Bedrock, "https://x.example", [ModelAuthKind.ApiKey])] }), "a key is not the platform's credentials");

        // Through the real service: contributed, chosen for Claude Code, launched with variables only.
        var state = Path.Combine(root, "cloud-platforms-store");
        Directory.CreateDirectory(state);
        List<ProviderDefinition> custom = [];
        List<ModelSourceChoice> sources = [];
        var service = new ModelProvidersService(new ProviderKeyStore(state), new ModelProviderState(() => custom, next => custom = [.. next], () => sources, next => sources = [.. next]));
        service.Contribute(new(BedrockManifest.Id, [bedrock]));
        service.Contribute(new(VertexManifest.Id, [vertex]));
        var report = service.Report();
        Require(report.Providers.Where(row => row.Provider.Contributor is "bedrock" or "vertex").All(row => row.Ready), "a keyless platform provider is ready at once");
        Require(ProviderLaunch.For(ProviderLaunch.Claude, report, windows: false) is null, "with no source chosen the Claude Code launch is its own");
        report = service.SetSource(new("claude-code", "bedrock"));
        var launch = ProviderLaunch.For(ProviderLaunch.Claude, report, windows: false) ?? throw new InvalidOperationException("cloud-platforms: a chosen platform contributes to the Claude Code launch");
        Require(launch.BlockedReason is null && launch.Arguments.Count == 0 && launch.Wrapper.Count == 0 && launch.Environment.OrderBy(pair => pair.Key).SequenceEqual(bedrock.Environment.OrderBy(pair => pair.Key)), "the launch only sets the platform's variables");
        var line = AgentLaunchComposition.Apply("claude --model opus", "claude", [launch]);
        Require(line.EndsWith(" claude --model opus", StringComparison.Ordinal) && line[..^" claude --model opus".Length].Split(' ').Order().SequenceEqual(["AWS_PROFILE=work", "AWS_REGION=eu-west-1", "CLAUDE_CODE_USE_BEDROCK=1"]),
            "the variables precede the claude command: " + line);
        Require(ProviderLaunch.For(ProviderLaunch.Claude, report, windows: true) is { BlockedReason: { Length: > 0 } }, "a Windows shell gets no variables, so the launch is blocked rather than run on another account");
        Require(new[] { "codex", "pi", "omp", "other" }.All(launcher => ProviderLaunch.For(launcher, report) is null), "no other launcher changes");
        report = service.SetSource(new("claude-code", "vertex"));
        Require(ProviderLaunch.For(ProviderLaunch.Claude, report, windows: false)!.Environment["ANTHROPIC_VERTEX_PROJECT_ID"] == "my-project-123", "Vertex launches with its project");

        // A source that is not a platform has no Claude Code seam: it stays recorded only.
        report = service.SetSource(new("claude-code", null));
        Require(ProviderLaunch.For(ProviderLaunch.Claude, report, windows: false) is null, "choosing the agent's own provider clears the launch");

        // Withdrawing (settings cleared, plugin stopped) removes the provider and the choice that used it.
        service.SetSource(new("claude-code", "vertex"));
        service.Withdraw(new(VertexManifest.Id));
        report = service.Report();
        Require(report.Providers.All(row => row.Provider.Id != "vertex") && report.Sources.All(choice => choice.AgentId != "claude-code") && ProviderLaunch.For(ProviderLaunch.Claude, report, windows: false) is null, "a withdrawn platform leaves the list, the choice and the launch");
        Console.WriteLine("PASS cloud platforms: manifests, settings to provider, safe values only, offered to Claude Code only, launch variables without a secret, Windows blocked, withdrawal");
    }
}