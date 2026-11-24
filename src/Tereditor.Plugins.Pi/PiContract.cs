using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Pi;

public sealed record PiNoParams;

/// <summary>Whether <c>pi</c> was found and which version it reports.</summary>
public sealed record PiStatus(bool Installed, string Command)
{
    public string? Version { get; init; }
    public string? Detail { get; init; }
    /// <summary>The source in use: <see cref="PiSources.System"/> or <see cref="PiSources.Managed"/>.</summary>
    public string Source { get; set; } = PiSources.System;
    /// <summary>The version tereditor pins for the managed Pi.</summary>
    public string PinnedVersion { get; set; } = "";
    /// <summary>The version of the downloaded copy, the pinned one or one from an earlier pin that an update replaces; empty when none is downloaded.</summary>
    public string ManagedVersion { get; set; } = "";
    /// <summary>Whether the pinned copy is downloading now.</summary>
    public bool Installing { get; init; }
    /// <summary>What the download is doing now, in plain words; empty when it is not running.</summary>
    public string Progress { get; set; } = "";
    /// <summary>Why the last download failed; empty when it did not.</summary>
    public string Error { get; set; } = "";
}

/// <summary>Where the Pi that a chat runs comes from.</summary>
public static class PiSources
{
    /// <summary>The <c>pi</c> command from the settings, found on PATH by default.</summary>
    public const string System = "system";
    /// <summary>The pinned Pi that tereditor downloads and runs on Bun (Runtimes for tooling).</summary>
    public const string Managed = "managed";
}

public sealed record PiSettings
{
    /// <summary>The executable; the system installation on PATH by default.</summary>
    public string Command { get; set; } = "pi";
    /// <summary><see cref="PiSources.System"/> (default) runs <see cref="Command"/>; <see cref="PiSources.Managed"/> runs the pinned Pi tereditor downloads, never the system one.</summary>
    public string Source { get; set; } = PiSources.System;
    /// <summary><c>--model</c>: a pattern or <c>provider/id</c>; blank uses Pi's default.</summary>
    public string Model { get; set; } = "";
    /// <summary>The thinking level a new chat starts with; blank uses Pi's default.</summary>
    public string Thinking { get; set; } = "";
    /// <summary><c>--tools +codemode</c>: adds Pi's codemode tool, which lets the model run scripts over Pi's other tools. Old settings without it get on.</summary>
    public bool Codemode { get; set; } = true;
}

public static class PiThinking
{
    /// <summary>The levels <c>set_thinking_level</c> accepts (Pi's RPC docs).</summary>
    public static readonly IReadOnlyList<string> Levels = ["off", "minimal", "low", "medium", "high", "xhigh"];
}

/// <summary>The scopes Pi's files come from: the user's agent directory (and home), or the working directory's `.pi` and its repository.</summary>
public static class PiScopes
{
    public const string User = "user";
    public const string Project = "project";
    /// <summary>Added by tereditor for one run (its own MCP server), not read from a file Pi owns.</summary>
    public const string Tereditor = "tereditor";
}

/// <summary>The kinds of resource the Settings › Pi and Context views list.</summary>
public static class PiConfigKinds
{
    public const string Context = "context";
    public const string Extension = "extension";
    public const string Skill = "skill";
    public const string Prompt = "prompt";
    public const string Mcp = "mcp";
    public const string Package = "package";
}

public sealed record PiConfigParams(string WorkspaceId);

/// <summary>One file or entry Pi would load. Loaded is false for a project resource while the project is not trusted.</summary>
public sealed record PiConfigItem(string Kind, string Scope, string Name, string Path, bool Loaded, string Detail);

/// <summary>A key of a settings.json (packages excluded): its value as Pi wrote it, shortened.</summary>
public sealed record PiConfigSetting(string Key, string Scope, string Value, bool Loaded);

/// <summary>A command the open Pi chat reports: its name, where it comes from and its description.</summary>
public sealed record PiConfigCommand(string Name, string Source, string Description);

/// <summary>
/// What the Pi pane shows for a workspace. Trust is read from trust.json and shown only; the chat fields come from the open Pi chat of the
/// workspace, when there is one.
/// </summary>
public sealed record PiConfigSnapshot(string Cwd, string AgentDir, string Trust, string TrustFile, IReadOnlyList<PiConfigItem> Items,
    IReadOnlyList<PiConfigSetting> Settings, IReadOnlyList<PiConfigCommand> Commands, bool ChatOpen)
{
    /// <summary>The model provider Pi runs under: the chosen source's name, or Pi's own provider.</summary>
    public string? Provider { get; init; }
}

public sealed record PiPackageParams(string WorkspaceId, string Source, bool Local);

/// <summary>The result of a package change: whether Pi succeeded, and its last output lines.</summary>
public sealed record PiPackageResult(bool Ok, string Output);

public static class PiContract
{
    public const int WireVersion = 1;

    public const string DocumentScheme = "pi-chat";

    public static readonly PluginMethod<PiNoParams, PiStatus> Status = new("status");

    /// <summary>Downloads the pinned Pi now and answers when it is done, failed or cancelled. One download runs at a time; a chat that needs it shares it.</summary>
    public static readonly PluginMethod<PiNoParams, PiStatus> Install = new("install");

    /// <summary>Cancels the running download.</summary>
    public static readonly PluginMethod<PiNoParams, PiStatus> CancelInstall = new("cancelInstall");

    /// <summary>Deletes the downloaded Pi.</summary>
    public static readonly PluginMethod<PiNoParams, PiStatus> Remove = new("remove");

    /// <summary>Reads what a Pi session in the workspace would load, plus the open chat's model and commands.</summary>
    public static readonly PluginMethod<PiConfigParams, PiConfigSnapshot> Config = new("config");

    /// <summary>`pi install &lt;source&gt;` (with `-l` for the project). The pane says to restart a chat to load the change.</summary>
    public static readonly PluginMethod<PiPackageParams, PiPackageResult> PackageInstall = new("packageInstall");

    /// <summary>`pi remove <source>` (with `-l` for the project).</summary>
    public static readonly PluginMethod<PiPackageParams, PiPackageResult> PackageRemove = new("packageRemove");

    /// <summary>The chat contract is shared with the other chat plugins: Pi serves its methods and its updates channel.</summary>
    public static readonly PluginContract Contract = PluginContract.Create<PiSettings>(PiManifest.Id, WireVersion,
        [.. ChatContract.Methods, Status, Install, CancelInstall, Remove, Config, PackageInstall, PackageRemove], [ChatContract.Updates], PiJson.Default, ChatContract.Json);
}

[JsonSerializable(typeof(PiSettings))]
[JsonSerializable(typeof(PiNoParams))]
[JsonSerializable(typeof(PiStatus))]
[JsonSerializable(typeof(PiConfigParams))]
[JsonSerializable(typeof(PiConfigItem))]
[JsonSerializable(typeof(PiConfigSetting))]
[JsonSerializable(typeof(PiConfigCommand))]
[JsonSerializable(typeof(PiConfigSnapshot))]
[JsonSerializable(typeof(PiPackageParams))]
[JsonSerializable(typeof(PiPackageResult))]
internal sealed partial class PiJson : JsonSerializerContext;