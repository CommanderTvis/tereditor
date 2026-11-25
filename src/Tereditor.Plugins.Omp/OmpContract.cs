using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Omp;

/// <summary>Where a context file applies: the omp user (or profile) directory, a directory above the launch directory, or the launch directory.</summary>
public enum OmpContextScope
{
    User,
    Ancestor,
    Project
}

/// <summary>The context files tereditor offers to create when missing.</summary>
public enum OmpContextTarget
{
    /// <summary><c>&lt;workspace&gt;/AGENTS.md</c>.</summary>
    [JsonStringEnumMemberName("agents")] Agents,
    /// <summary><c>&lt;workspace&gt;/.omp/RULES.md</c>.</summary>
    [JsonStringEnumMemberName("rules")] Rules,
    /// <summary><c>&lt;workspace&gt;/.omp/APPEND_SYSTEM.md</c>.</summary>
    [JsonStringEnumMemberName("append-system")] AppendSystem
}

/// <summary>Whether <c>omp</c> was found and which version it reports.</summary>
/// <param name="Installed">Whether <c>omp --version</c> ran and exited cleanly.</param>
public sealed record OmpStatus(bool Installed, string Command)
{
    public string? Version { get; init; }
    public string? Detail { get; init; }
    /// <summary>The source in use now (<see cref="OmpSources.System"/> or <see cref="OmpSources.Managed"/>), after <see cref="OmpSettings.Source"/> is resolved.</summary>
    public string Source { get; set; } = OmpSources.System;
    /// <summary>Whether omp can be started now: it is installed, or it is the managed one and Bun is there to download and run it. A managed omp that is not downloaded yet is usable.</summary>
    public bool Usable { get; init; }
    /// <summary>The executable word a launch line starts with: the host's <c>omp</c> program that runs the managed copy on Bun; empty when the system command is used.</summary>
    public string LaunchCommand { get; set; } = "";
    /// <summary>The version tereditor pins for the managed omp.</summary>
    public string PinnedVersion { get; set; } = "";
    /// <summary>The version of the downloaded copy, the pinned one or one from an earlier pin that an update replaces; empty when none is downloaded.</summary>
    public string ManagedVersion { get; set; } = "";
    /// <summary>Whether the pinned copy is downloading now.</summary>
    public bool Installing { get; init; }
    /// <summary>What the download is doing now, in plain words; empty when it is not running.</summary>
    public string Progress { get; set; } = "";
    /// <summary>Why the last download failed; empty when it did not.</summary>
    public string Error { get; set; } = "";
    /// <summary>A plain sentence about why the source in use is not the one chosen (Bun is missing, so the default fell back to the system command); empty otherwise.</summary>
    public string SourceNote { get; set; } = "";
    /// <summary>The tereditor profile's MCP file, when launches use the profile.</summary>
    public string? ProfileMcpPath { get; init; }
}

/// <summary>
/// One context file row. <paramref name="Depth"/> is zero for a discovered file and the import hop for an
/// <c>@</c>-import, which follows its importer in the list.
/// </summary>
/// <param name="Provider">The omp provider that discovers it: native, claude, agents, codex, gemini, opencode, github or agents-md; <c>import</c> for an import.</param>
public sealed record OmpContextFile(string Label, string Path, OmpContextScope Scope, string Provider, long Bytes, int Depth)
{
    /// <summary>A rough token estimate, about four bytes per token.</summary>
    public long Tokens => (Bytes + 3) / 4;
    /// <summary>Another provider at the same scope and depth wins, so omp does not read this file.</summary>
    public bool Shadowed { get; init; }
    /// <summary>For an import: whether it was skipped because it was already on the import chain.</summary>
    public bool Cycle { get; init; }
}

public sealed record OmpContextOffer(OmpContextTarget Target, string Label, string RelativePath);

/// <summary>The context files omp would read for a launch in <paramref name="Root"/>, most general first, with the offers for missing ones.</summary>
/// <param name="UserDirectory">The agent directory user-level files come from: the tereditor profile's when launches use it.</param>
public sealed record OmpContextSnapshot(string Root, string UserDirectory, IReadOnlyList<OmpContextFile> Files, IReadOnlyList<OmpContextOffer> Offers);

public sealed record OmpNoParams;

public sealed record OmpWorkspaceParams(string WorkspaceId);

public sealed record OmpCreateParams(string WorkspaceId, OmpContextTarget Target);

/// <summary>The outcome of copying the default omp profile's sign-in into tereditor's profile; the message carries no credentials.</summary>
public sealed record OmpSignInResult(bool Success, string Message);

/// <summary>Where the omp that tereditor starts comes from.</summary>
public static class OmpSources
{
    /// <summary>The <c>omp</c> command from the settings, found on PATH by default.</summary>
    public const string System = "system";
    /// <summary>The pinned omp that tereditor downloads and runs on Bun (Runtimes for tooling).</summary>
    public const string Managed = "managed";

    /// <summary>
    /// The source a launch uses. A choice the user made stands; none (<c>""</c>) is managed while Bun is there or can be downloaded, and the system
    /// command otherwise.
    /// </summary>
    public static string Effective(string chosen, bool bunAvailable) => chosen switch
    {
        Managed => Managed,
        System => System,
        _ => bunAvailable ? Managed : System
    };
}

public sealed record OmpSettings
{
    /// <summary>The executable the system source runs; the installation on PATH by default.</summary>
    public string Command { get; set; } = "omp";
    /// <summary>
    /// <see cref="OmpSources.Managed"/> runs the pinned omp tereditor downloads on Bun, <see cref="OmpSources.System"/> runs <see cref="Command"/>.
    /// Blank (the default) is the managed one while Bun from Runtimes for tooling is available, the system command otherwise.
    /// </summary>
    public string Source { get; set; } = "";
    /// <summary><c>--model</c>: a model id, <c>provider/model</c> or <c>@role</c>; blank uses omp's default.</summary>
    public string Model { get; set; } = "";
    /// <summary><c>--approval-mode</c>: <c>always-ask</c>, <c>always-ask-cached</c>, <c>write</c> or <c>yolo</c>; blank uses omp's configuration.</summary>
    public string ApprovalMode { get; set; } = "";
    /// <summary><c>--advisor</c>.</summary>
    public bool Advisor { get; init; }
    /// <summary>Launch with tereditor's profile, whose MCP file names tereditor's server.</summary>
    public bool Mcp { get; set; } = true;
    /// <summary>Keep omp's own language servers; off passes <c>--no-lsp</c> so sessions use tereditor's.</summary>
    public bool OwnCodeIntelligence { get; init; }
}

public static class OmpContract
{
    public const int WireVersion = 1;

    public static readonly PluginMethod<OmpNoParams, OmpStatus> Status = new("status");
    public static readonly PluginMethod<OmpWorkspaceParams, OmpContextSnapshot> Context = new("context");
    public static readonly PluginMethod<OmpCreateParams, OmpContextSnapshot> CreateContextFile = new("createContextFile");
    /// <summary>Pipes <c>omp auth export</c> (default profile) into <c>omp --profile tereditor auth import -</c> on the host.</summary>
    public static readonly PluginMethod<OmpNoParams, OmpSignInResult> ImportSignIn = new("importSignIn");

    /// <summary>Downloads the pinned omp now and answers when it is done, failed or canceled. One download runs at a time.</summary>
    public static readonly PluginMethod<OmpNoParams, OmpStatus> Install = new("install");

    /// <summary>Cancels the running download.</summary>
    public static readonly PluginMethod<OmpNoParams, OmpStatus> CancelInstall = new("cancelInstall");

    /// <summary>Deletes the downloaded omp.</summary>
    public static readonly PluginMethod<OmpNoParams, OmpStatus> Remove = new("remove");

    public static readonly PluginContract Contract = PluginContract.Create<OmpSettings>(OmpManifest.Id, WireVersion,
        [Status, Context, CreateContextFile, ImportSignIn, Install, CancelInstall, Remove], [], OmpJson.Default);
}

[JsonSourceGenerationOptions(Converters = [typeof(PluginJson.EnumConverter<OmpContextScope>), typeof(PluginJson.EnumConverter<OmpContextTarget>)])]
[JsonSerializable(typeof(OmpSettings))]
[JsonSerializable(typeof(OmpNoParams))]
[JsonSerializable(typeof(OmpStatus))]
[JsonSerializable(typeof(OmpWorkspaceParams))]
[JsonSerializable(typeof(OmpContextSnapshot))]
[JsonSerializable(typeof(OmpCreateParams))]
[JsonSerializable(typeof(OmpSignInResult))]
internal sealed partial class OmpJson : JsonSerializerContext;