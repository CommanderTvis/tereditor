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
}

public static class PiThinking
{
    /// <summary>The levels <c>set_thinking_level</c> accepts (Pi's RPC docs).</summary>
    public static readonly IReadOnlyList<string> Levels = ["off", "minimal", "low", "medium", "high", "xhigh"];
}

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

    /// <summary>The chat contract is shared with the other chat plugins: Pi serves its methods and its updates channel.</summary>
    public static readonly PluginContract Contract = PluginContract.Create<PiSettings>(PiManifest.Id, WireVersion,
        [.. ChatContract.Methods, Status, Install, CancelInstall, Remove], [ChatContract.Updates], PiJson.Default, ChatContract.Json);
}

[JsonSerializable(typeof(PiSettings))]
[JsonSerializable(typeof(PiNoParams))]
[JsonSerializable(typeof(PiStatus))]
internal sealed partial class PiJson : JsonSerializerContext;