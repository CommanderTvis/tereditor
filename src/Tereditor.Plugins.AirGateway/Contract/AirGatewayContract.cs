using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.AirGateway;

/// <summary>The plugin's settings namespace. It holds nothing: which agents Central governs is each agent's Model source (Model providers).</summary>
public sealed record AirSettings;

/// <param name="Probe">Asks <c>central status</c> for the sign-in and proxy rows; without it only the binary and its version are read.</param>
public sealed record AirStatusParams(bool Probe = false);

public sealed record AirActionParams;

public sealed record AirSwitchParams(string SourceId);

/// <param name="State">absent, outdated, supported, malformed-version or probe-failed.</param>
/// <param name="Auth">connected, signed-out or unknown.</param>
/// <param name="Proxy">running, stopped or unknown.</param>
/// <param name="InstallCommand">What the user runs themselves to install Central on this host's platform.</param>
public sealed record AirCentral(string State, string Auth, string Proxy, string InstallCommand, string? Path = null, string? Version = null);

/// <param name="Id">The agent plugin's id.</param>
/// <param name="Chosen">Whether the agent's Model source is JetBrains (Air Gateway).</param>
/// <param name="Launch">The exact command shape prefixed to this agent's launches, or <see langword="null"/> when its launches are unchanged.</param>
public sealed record AirAgent(string Id, string Label, bool Chosen, string? Launch = null);

/// <param name="Remaining">Credits left of the chosen central AI source's recurring quota.</param>
/// <param name="Total">The recurring quota.</param>
/// <param name="TopUp">Credits left of the top-up quota; zero when there is none.</param>
/// <param name="RefillNext">When the quota refills, Unix milliseconds, or <see langword="null"/> when Central did not say.</param>
/// <param name="ReadAt">When the host read them from Central, Unix milliseconds.</param>
public sealed record AirCredits(double Remaining, double Total, double TopUp = 0, long? RefillNext = null, long? ReadAt = null);

/// <param name="Credits">Only while Central governs an agent and is signed in, and only when the last check could read them; otherwise <see langword="null"/>.</param>
public sealed record AirStatus(AirCentral Central, IReadOnlyList<AirAgent> Agents, bool Busy = false, string? Detail = null, AirCredits? Credits = null);

public sealed record AirOutcome(bool Ok, string Message, AirStatus Status);

/// <param name="Name">Shown to the user so they can tell the sources apart; sanitized, never parsed.</param>
public sealed record AirAccessSource(string Id, string Name, bool Current);

public sealed record AirAccessList(IReadOnlyList<AirAccessSource> Sources, string? Detail = null);

/// <summary>One agent Central can govern: its plugin id, the launcher id its launches ask with, its name and the name `central run` takes.</summary>
public sealed record AirAgentWiring(string AgentId, string LauncherId, string Label, string CentralName);

/// <summary>
/// What the plugin contributes to the agents' launches. An agent whose Model source is JetBrains (Air Gateway) runs
/// <c>central run &lt;agent&gt; -- ...</c>, which starts the agent through JetBrains Air for that launch only and leaves the agent's own configuration alone.
/// </summary>
public static class AirLaunch
{
    /// <summary>The agents Central declares good wiring for. Any other agent is not offered the source.</summary>
    public static readonly IReadOnlyList<AirAgentWiring> Agents =
    [
        new("claude-code", "claude", "Claude Code", "claude-code"),
        new("codex", "codex", "Codex", "codex"),
        new("pi", "pi", "Pi", "pi")
    ];

    /// <summary>The provider the plugin adds to Model providers: no key, no API, wired to exactly <see cref="Agents"/>.</summary>
    public static ProviderDefinition Provider { get; } = new(ProviderCatalog.JetBrainsAi, "JetBrains (Air Gateway)", ProviderGroup.JetBrainsAi, [])
    {
        NeedsKey = false,
        Links = new("https://www.jetbrains.com/air/"),
        Note = "Starts the agent through JetBrains Central with your JetBrains AI access. It applies to terminals and chats you start afterwards.",
        WiredAgents = [.. Agents.Select(agent => agent.AgentId)]
    };

    /// <summary>The agents whose Model source is JetBrains (Air Gateway), among those it wires.</summary>
    /// <param name="sources">The model sources Model providers reports.</param>
    public static IReadOnlySet<string> Chosen(IEnumerable<ModelSourceChoice> sources) =>
        sources.Where(choice => choice.ProviderId == ProviderCatalog.JetBrainsAi && Agents.Any(agent => agent.AgentId == choice.AgentId)).Select(choice => choice.AgentId).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether Central governs at least one agent.</summary>
    public static bool Wired(IReadOnlySet<string> chosen) => chosen.Count > 0;

    public static bool Enabled(IReadOnlySet<string> chosen, string launcherId) =>
        Agents.FirstOrDefault(agent => agent.LauncherId == launcherId) is { } wiring && chosen.Contains(wiring.AgentId);

    /// <summary>
    /// The contribution for a launcher, or <see langword="null"/> when the agent's Model source is not JetBrains (Air Gateway) or the
    /// launcher is unknown. Once chosen it always governs, failing closed: the agent is wrapped with the bare name when Central has
    /// not been found, so a missing CLI fails the launch in the shell.
    /// </summary>
    public static AgentLaunchContribution? Contribution(IReadOnlySet<string> chosen, string launcherId, string? centralPath) =>
        Enabled(chosen, launcherId)
            ? new() { Wrapper = [centralPath is { Length: > 0 } ? centralPath : "central", "run", Agents.First(agent => agent.LauncherId == launcherId).CentralName, "--"], ReplacesExecutable = true }
            : null;

    /// <summary>The command shape shown to the user: the wrapper followed by the agent's usual arguments.</summary>
    public static string? Shape(IReadOnlySet<string> chosen, string launcherId, string? centralPath) =>
        Contribution(chosen, launcherId, centralPath) is { } contribution ? string.Join(' ', contribution.Wrapper) + " <" + launcherId + " arguments>" : null;
}

public static class AirGatewayContract
{
    public const string Id = "air-gateway";

    public static readonly PluginMethod<AirStatusParams, AirStatus> Status = new("status");
    public static readonly PluginMethod<AirActionParams, AirOutcome> SignIn = new("sign-in");
    public static readonly PluginMethod<AirActionParams, AirOutcome> SignOut = new("sign-out");
    public static readonly PluginMethod<AirActionParams, AirOutcome> StartProxy = new("start-proxy");
    public static readonly PluginMethod<AirActionParams, AirOutcome> Update = new("update");
    public static readonly PluginMethod<AirActionParams, AirAccessList> AccessSources = new("access-sources");
    public static readonly PluginMethod<AirSwitchParams, AirOutcome> SwitchAccess = new("switch-access");
    public static readonly PluginChannel<AirStatus> Changed = PluginChannel<AirStatus>.State("status", Status);

    public static readonly PluginContract Contract = PluginContract.Create<AirSettings>(Id, 1,
        [Status, SignIn, SignOut, StartProxy, Update, AccessSources, SwitchAccess], [Changed], AirGatewayJson.Default);

    public static readonly PluginManifest Manifest = new(Id, "Air Gateway", "", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.ModelProviders,
        // Runs Central's command line on the host's operating system, which a browser tab does not have.
        UnavailableOn = [HostKind.PureWeb],
        DependsOn = [new(ProvidersManifest.Id, ProvidersContract.WireVersion)],
        Description = "Adds JetBrains (Air Gateway) to Model providers. An agent that chooses it (Claude Code, Codex or Pi) starts through JetBrains Air Gateway with the Central CLI, per launch.",
        EnabledByDefault = false,
        Headless = HeadlessSupport.No,
        Host = "Tereditor.Plugins.AirGateway.Host.dll",
        Ui = "Tereditor.Plugins.AirGateway.UI.dll"
    };
}

[JsonSerializable(typeof(AirSettings))]
[JsonSerializable(typeof(AirStatusParams))]
[JsonSerializable(typeof(AirStatus))]
[JsonSerializable(typeof(AirActionParams))]
[JsonSerializable(typeof(AirOutcome))]
[JsonSerializable(typeof(AirAccessList))]
[JsonSerializable(typeof(AirSwitchParams))]
internal sealed partial class AirGatewayJson : JsonSerializerContext;