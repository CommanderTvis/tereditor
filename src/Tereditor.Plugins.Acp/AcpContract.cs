using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Acp;

public sealed record AcpSettings
{
    /// <summary>The agent a new chat starts, by entry id; blank asks the user.</summary>
    public string DefaultAgent { get; set; } = "";
}

/// <summary>An agent the user can chat with: how to start it. Registry installs fill <see cref="Version"/> and <see cref="Source"/>; custom agents leave them blank.</summary>
public sealed record AcpAgentEntry(string Id, string Name, string Command)
{
    public IReadOnlyList<string> Args { get; set; } = [];
    public IReadOnlyDictionary<string, string> Env { get; set; } = new Dictionary<string, string>();
    /// <summary>The directory the agent runs in; blank is the workspace.</summary>
    public string Cwd { get; set; } = "";
    /// <summary>"custom" or "registry".</summary>
    public string Origin { get; set; } = "custom";
    public string? Version { get; init; }
    public string? Description { get; init; }
    /// <summary>Whether the agent is offered by the launcher. A turned-off agent stays configured.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The program to run instead of <see cref="Command"/> (a registry install keeps its own); blank uses <see cref="Command"/>.</summary>
    public string CommandOverride { get; set; } = "";
    /// <summary>For a registry agent: true runs the registry's <see cref="Args"/>, false runs <see cref="ArgsOverride"/>. A custom agent's own <see cref="Args"/> are always used.</summary>
    public bool UseDefaultArgs { get; set; } = true;
    public IReadOnlyList<string> ArgsOverride { get; set; } = [];
    /// <summary>What the registry said about the agent when it was installed.</summary>
    public string? Website { get; init; }
    public string? Repository { get; init; }
    public string? License { get; init; }
    /// <summary>Where the code comes from: the package spec or the archive url.</summary>
    public string? Source { get; init; }
    /// <summary>What was checked when it was installed (see <see cref="AcpInstallPlan.Verification"/>); null for an install made before this was recorded.</summary>
    public string? Verification { get; init; }
    /// <summary>The mode a new chat with this agent switches to once the agent lists its modes; blank keeps the agent's own.</summary>
    public string DefaultMode { get; set; } = "";
    /// <summary>The modes the agent listed the last time a chat started. The host fills it; the settings page offers these for <see cref="DefaultMode"/>.</summary>
    public IReadOnlyList<AcpKnownMode> KnownModes { get; set; } = [];
    /// <summary>What tereditor can give the agent, from its last start: "both", "tools" (MCP only), "instructions" (system prompt only) or "none"; blank before its first chat. The host fills it.</summary>
    public string TereditorSupport { get; set; } = "";

    /// <summary>The program that starts: the override when there is one.</summary>
    public string LaunchCommand() => CommandOverride.Length > 0 ? CommandOverride : Command;

    /// <summary>The arguments that start it: a registry agent's override unless it uses the defaults.</summary>
    public IReadOnlyList<string> LaunchArgs() => Origin == "registry" && !UseDefaultArgs ? ArgsOverride : Args;
}

public sealed record AcpKnownMode(string Id, string Name);

public sealed record AcpNoParams;

public sealed record AcpAgentList(IReadOnlyList<AcpAgentEntry> Agents)
{
    /// <summary>What is wrong with an agent, by id: its program is missing, or its last start failed. Absent when nothing is known to be wrong.</summary>
    public IReadOnlyDictionary<string, string> Problems { get; set; } = new Dictionary<string, string>();
}

/// <summary>Chooses which agent a chat talks to; sent before the chat is opened.</summary>
public sealed record AcpChooseParams(string WorkspaceId, string ChatId, string AgentId);

/// <summary>Asks for the registry agents' icons. <paramref name="CacheOnly"/> reads only what is already on disk (for a menu that must open at once).</summary>
public sealed record AcpIconParams(bool Refresh, bool CacheOnly);

/// <summary>The icons that can be shown, by registry agent id: SVG text that passed the host's checks. An agent without an entry keeps the generic glyph.</summary>
public sealed record AcpIconSet(IReadOnlyDictionary<string, string> Svgs);

/// <summary>Asks which agent each chat of a workspace talks to, so a restored tab can show that agent's logo.</summary>
public sealed record AcpChatAgentsParams(string WorkspaceId);

/// <summary>The agent entry id of each chat of the workspace that has chosen one, by chat id.</summary>
public sealed record AcpChatAgents(IReadOnlyDictionary<string, string> Agents);

/// <summary>Asks for the icon of one registry agent whose chat just started; the host fetches it when the disk has none.</summary>
public sealed record AcpAgentIconParams(string AgentId);

public static class AcpContract
{
    public const int WireVersion = 1;

    public const string DocumentScheme = "acp-chat";

    public static readonly PluginMethod<AcpNoParams, AcpAgentList> Agents = new("agents");
    public static readonly PluginMethod<AcpAgentEntry, AcpAgentList> AddAgent = new("addAgent");
    public static readonly PluginMethod<AcpChooseParams, Ack> Choose = new("chooseAgent");
    public static readonly PluginMethod<AcpRegistryParams, AcpRegistryView> Registry = new("registry");
    /// <summary>What installing would download and run: shown to the user before <see cref="Install"/>.</summary>
    public static readonly PluginMethod<AcpInstallPlanParams, AcpInstallPlan> InstallPlan = new("installPlan");
    public static readonly PluginMethod<AcpInstallRequest, AcpAgentList> Install = new("install");
    public static readonly PluginMethod<AcpChooseParams, AcpAgentList> RemoveAgent = new("removeAgent");
    /// <summary>The registry agents' icons, fetched by the host from the registry's own origin and checked there.</summary>
    public static readonly PluginMethod<AcpIconParams, AcpIconSet> Icons = new("icons");
    /// <summary>The icon of one registry agent: from disk, or fetched once per run when a chat of it started. Empty for a custom agent or on failure.</summary>
    public static readonly PluginMethod<AcpAgentIconParams, AcpIconSet> AgentIcon = new("agentIcon");
    /// <summary>Which agent each chat of a workspace talks to.</summary>
    public static readonly PluginMethod<AcpChatAgentsParams, AcpChatAgents> ChatAgents = new("chatAgents");
    /// <summary>Where an agent's sign-in comes from: names and accounts the host detected, never a secret.</summary>
    public static readonly PluginMethod<AcpSignInParams, AcpSignInInfo> SignIn = new("signIn");

    /// <summary>The chat contract is shared with the other chat plugins: ACP serves its methods and its updates channel.</summary>
    public static readonly PluginContract Contract = PluginContract.Create<AcpSettings>(AcpManifest.Id, WireVersion,
        [.. ChatContract.Methods, ChatContract.SetOption, Agents, AddAgent, Choose, Registry, InstallPlan, Install, RemoveAgent, Icons, AgentIcon, ChatAgents, SignIn], [ChatContract.Updates],
        AcpJson.Default, ChatContract.Json);
}

[JsonSerializable(typeof(AcpSettings))]
[JsonSerializable(typeof(AcpNoParams))]
[JsonSerializable(typeof(AcpAgentList))]
[JsonSerializable(typeof(AcpAgentEntry))]
[JsonSerializable(typeof(AcpChooseParams))]
[JsonSerializable(typeof(Ack))]
[JsonSerializable(typeof(AcpRegistryParams))]
[JsonSerializable(typeof(AcpRegistryView))]
[JsonSerializable(typeof(AcpInstallPlanParams))]
[JsonSerializable(typeof(AcpInstallPlan))]
[JsonSerializable(typeof(AcpInstallRequest))]
[JsonSerializable(typeof(AcpIconParams))]
[JsonSerializable(typeof(AcpIconSet))]
[JsonSerializable(typeof(AcpAgentIconParams))]
[JsonSerializable(typeof(AcpChatAgentsParams))]
[JsonSerializable(typeof(AcpChatAgents))]
[JsonSerializable(typeof(AcpSignInParams))]
[JsonSerializable(typeof(AcpSignInInfo))]
internal sealed partial class AcpJson : JsonSerializerContext;