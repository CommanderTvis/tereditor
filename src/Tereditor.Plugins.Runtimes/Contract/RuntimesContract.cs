using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Runtimes;

/// <summary>The plugin's settings (Settings › Runtimes for tooling): the programs the user chose over any other.</summary>
public sealed record RuntimesSettings
{
    /// <summary>The absolute path of the Bun the user wants to use; empty means find one.</summary>
    public string BunPath { get; set; } = "";

    /// <summary>The absolute path of the uv the user wants to use; empty means find one.</summary>
    public string UvPath { get; set; } = "";
}

/// <summary>One runtime as the settings page shows it.</summary>
/// <param name="Kind"><c>bun</c> or <c>uv</c>.</param>
/// <param name="Name">Bun or uv.</param>
/// <param name="Source">The source in use: <c>pinned</c>, <c>system</c> or <c>downloaded</c>; empty when none is available.</param>
/// <param name="Path">The program in use; empty when none.</param>
/// <param name="Version">Its version; empty when unknown.</param>
/// <param name="Pinned">The path the user pinned; empty when none.</param>
/// <param name="DownloadBytes">The disk space of the downloaded copy and its caches.</param>
/// <param name="Downloaded">Whether a downloaded copy exists.</param>
/// <param name="Message">A plain sentence about the state.</param>
public sealed record RuntimeRow(string Kind, string Name, string Source, string Path, string Version, string Pinned, long DownloadBytes, bool Downloaded, string Message)
{
    /// <summary>The program of the downloaded copy, whichever source is in use; empty when none.</summary>
    public string DownloadedPath { get; init; } = "";
}

/// <param name="Error">What went wrong with the last Download now or Remove download, in plain words; empty when nothing did.</param>
public sealed record RuntimesState(IReadOnlyList<RuntimeRow> Runtimes, string Error = "");

public sealed record RuntimesStatusParams;

/// <param name="Kind"><c>bun</c> or <c>uv</c>.</param>
public sealed record RuntimeKindParams(string Kind);

/// <summary>One download waiting for the user's answer: <paramref name="Kind"/> is <c>bun</c> or <c>uv</c>.</summary>
public sealed record RuntimeAsk(string Kind, string Name, string Version);

/// <summary>The downloads that wait for an answer now. Every client sees the same list.</summary>
public sealed record RuntimeConsents(IReadOnlyList<RuntimeAsk> Pending);

/// <param name="Download">True to download, false for Not now.</param>
public sealed record RuntimeAnswerParams(string Kind, bool Download);

public static class RuntimesContract
{
    /// <summary>The state of both runtimes. Starts no download.</summary>
    public static readonly PluginMethod<RuntimesStatusParams, RuntimesState> Status = new("status");

    /// <summary>Downloads the pinned copy now.</summary>
    public static readonly PluginMethod<RuntimeKindParams, RuntimesState> Download = new("download");

    /// <summary>Stops what runs from the downloaded copy and deletes it with its caches.</summary>
    public static readonly PluginMethod<RuntimeKindParams, RuntimesState> Remove = new("remove");

    /// <summary>The downloads that wait for an answer.</summary>
    public static readonly PluginMethod<RuntimesStatusParams, RuntimeConsents> Consents = new("consents");

    /// <summary>Answers a waiting download: yes starts it, no remembers Not now until tereditor restarts or the user presses Download now.</summary>
    public static readonly PluginMethod<RuntimeAnswerParams, RuntimeConsents> Answer = new("answer");

    /// <summary>A download is waiting for an answer, or was answered.</summary>
    public static readonly PluginChannel<RuntimeConsents> ConsentChanged = PluginChannel<RuntimeConsents>.State("consent", Consents);

    public static readonly PluginContract Contract = PluginContract.Create<RuntimesSettings>(RuntimesPlugin.Id, 1, [Status, Download, Remove, Consents, Answer], [ConsentChanged], RuntimesJson.Default);
}

public static class RuntimesPlugin
{
    public const string Id = "runtimes";
    public const string Name = "Runtimes for tooling";

    public static readonly PluginManifest Manifest = new(Id, Name, "settings-3-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Environment,
        Description = "Gives language servers and agents the Bun and uv they need to run packages. It uses your own install when there is one and downloads its own copy only when a plugin asks for it. Turning it off deletes everything it downloaded.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.Yes,
        UnavailableOn = [HostKind.PureWeb],
        Host = "Tereditor.Plugins.Runtimes.Host.dll",
        Ui = "Tereditor.Plugins.Runtimes.UI.dll"
    };
}

[JsonSerializable(typeof(RuntimesSettings))]
[JsonSerializable(typeof(RuntimesStatusParams))]
[JsonSerializable(typeof(RuntimeKindParams))]
[JsonSerializable(typeof(RuntimesState))]
[JsonSerializable(typeof(RuntimeConsents))]
[JsonSerializable(typeof(RuntimeAnswerParams))]
internal sealed partial class RuntimesJson : JsonSerializerContext;