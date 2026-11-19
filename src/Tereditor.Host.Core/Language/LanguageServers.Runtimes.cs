using System.Text.RegularExpressions;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core.Language;

/// <summary>
/// The processes started from a downloaded runtime that a plugin told the host about. Turning the Runtimes for tooling plugin off stops them before
/// their program is deleted. Only the downloaded copy is ever stopped: a pinned or system install belongs to the user.
/// </summary>
internal static class ManagedProcesses
{
    private static readonly Lock Gate = new();
    private static readonly List<(int Pid, DateTime Started, string Program)> Tracked = [];

    internal static void Track(int pid, string program)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            lock (Gate)
            {
                Tracked.RemoveAll(item => item.Pid == pid);
                Tracked.Add((pid, process.StartTime.ToUniversalTime(), Path.GetFullPath(program)));
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Stops every tracked process whose program lies under <paramref name="root"/>, with its children, and waits briefly for each. Returns how many were stopped.</summary>
    internal static int StopUnder(string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        List<(int Pid, DateTime Started, string Program)> mine;
        lock (Gate)
        {
            mine = [.. Tracked.Where(item => item.Program.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))];
            Tracked.RemoveAll(mine.Contains);
        }
        var stopped = 0;
        foreach (var (pid, started, _) in mine)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                // A reused process id is not ours.
                if (Math.Abs((process.StartTime.ToUniversalTime() - started).TotalSeconds) > 2) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                stopped++;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return stopped;
    }
}

internal sealed partial class LanguageServers
{
    /// <summary>Whether the Runtimes for tooling plugin may download a runtime. A host without that plugin allows it.</summary>
    internal Func<bool> RuntimesEnabled { get; set; } = () => true;

    /// <summary>The path the user pinned for a runtime's program in Settings › Runtimes for tooling; empty when none.</summary>
    internal Func<string, string> RuntimePin { get; set; } = _ => "";

    /// <summary>Asks the user before a runtime that Runtimes for tooling provides is downloaded for a plugin's request; null (a host without a screen, or without the plugin) allows it.</summary>
    internal Func<RuntimeOffer, CancellationToken, ValueTask<bool>>? RuntimeConsent { get; set; }

    private async Task<bool> ConsentAsync(ManagedRuntimeKind kind, ManagedRuntimePin pin, CancellationToken token)
    {
        if (!kind.Provisioned || RuntimeConsent is not { } ask) return true;
        var asked = kind.Id switch { "bun" => (RuntimeKind?)RuntimeKind.Bun, "uv" => RuntimeKind.Uv, _ => null };
        return asked is not { } which || await ask(new(which, kind.Name, pin.Version), token);
    }

    internal static string DeclinedProblem(ManagedRuntimeKind kind) =>
        $"{kind.Name} is not downloaded because you chose Not now. Use Download now in Settings > Runtimes, or install {kind.Name} yourself.";

    private bool Usable(ManagedRuntimeKind kind) => !kind.Provisioned || RuntimesEnabled();

    internal static string OffProblem(ManagedRuntimeKind kind) =>
        $"{kind.Name} is not installed and Runtimes for tooling is off, so it is not downloaded. Turn it on in Settings > Plugins, or install {kind.Name} yourself.";

    internal static string ExecutableOf(RuntimeKind kind) => kind == RuntimeKind.Bun ? "bun" : "uv";

    internal static string NameOf(RuntimeKind kind) => kind == RuntimeKind.Bun ? "Bun" : "uv";

    private static string HomeOf(string cache, RuntimeKind kind) => Path.Combine(cache, kind == RuntimeKind.Bun ? "bun-home" : "uv-home");

    private string DownloadRoot(RuntimeKind kind) => Path.Combine(ManagedRuntimes.RuntimesRoot(directory), ExecutableOf(kind));

    /// <summary>
    /// The environment every start of a runtime gets: its cache stays in the host's cache (for the downloaded copy; an install of the user's keeps its own)
    /// and configuration files in the working directory are ignored. Bun reads <c>bunfig.toml</c> (it can preload scripts) and <c>.env</c> from the working
    /// directory; uv reads <c>uv.toml</c>, <c>pyproject.toml</c> and <c>.env</c> there. A workspace must not run code or pick indexes through them.
    /// </summary>
    internal IReadOnlyDictionary<string, string> RuntimeEnvironment(RuntimeKind kind, RuntimeSource source)
    {
        var cache = ManagedRuntimes.CacheRoot(directory);
        var home = HomeOf(cache, kind);
        if (kind == RuntimeKind.Bun)
        {
            var config = Path.Combine(home, "empty-bunfig.toml");
            Directory.CreateDirectory(home);
            if (!File.Exists(config)) File.WriteAllText(config, "");
            return new Dictionary<string, string> { ["BUN_INSTALL"] = home, ["BUN_OPTIONS"] = $"\"--config={config}\" --no-env-file", ["DO_NOT_TRACK"] = "1" };
        }
        var env = new Dictionary<string, string> { ["UV_NO_CONFIG"] = "1", ["UV_NO_ENV_FILE"] = "1" };
        if (source == RuntimeSource.Downloaded)
        {
            env["UV_CACHE_DIR"] = Path.Combine(home, "cache");
            env["UV_TOOL_DIR"] = Path.Combine(home, "tools");
            env["UV_TOOL_BIN_DIR"] = Path.Combine(home, "bin");
            env["UV_PYTHON_INSTALL_DIR"] = Path.Combine(home, "python");
            env["UV_PYTHON_BIN_DIR"] = Path.Combine(home, "bin");
        }
        return env;
    }

    private RuntimeResolution Ready(RuntimeKind kind, string path, RuntimeSource source) =>
        new(kind, true, path, kind == RuntimeKind.Bun ? ["x"] : ["tool", "run"], RuntimeEnvironment(kind, source), source, "", false);

    /// <summary>
    /// Finds a runtime: the path the user pinned, then an install on PATH (not under a managed toolchain, which means only the host's copies),
    /// then the downloaded copy, downloading it when the Runtimes for tooling plugin is on and has none yet.
    /// </summary>
    internal async Task<RuntimeResolution> ResolveRuntimeAsync(RuntimeKind kind, string workspace, CancellationToken token)
    {
        var tool = ExecutableOf(kind);
        var name = NameOf(kind);
        var runtime = ManagedRuntimes.KindOf(tool)!;
        if (ToolOverride(tool) is { Length: > 0 } pinned)
            return LanguageExecutables.Reject(pinned, workspace) is { } problem
                ? RuntimeResolution.Unavailable(kind, $"The {name} path you set cannot run. {problem}")
                : Ready(kind, pinned, RuntimeSource.Pinned);
        var rejected = new List<string>();
        if (Toolchain != ToolchainSource.Managed && LanguageExecutables.FindOnPath(tool, workspace, rejected) is { } found) return Ready(kind, found, RuntimeSource.System);
        if (!RuntimesEnabled()) return RuntimeResolution.Unavailable(kind, OffProblem(runtime), offerEnable: true);
        var platform = ManagedArtifacts.Platform();
        if (ManagedRuntimes.FindInstalled(directory, tool, platform, workspace) is { } copy) return Ready(kind, copy, RuntimeSource.Downloaded);
        if (ManagedRuntimes.Choose(runtime.Id, platform, workspace) is not { } pin) return RuntimeResolution.Unavailable(kind, ManagedRuntimes.NotPinned(runtime, platform, workspace));
        if (Toolchains.Offline) return RuntimeResolution.Unavailable(kind, $"{name} is not installed, and tereditor is offline, so it cannot be downloaded.");
        if (!await ConsentAsync(runtime, pin, token)) return RuntimeResolution.Unavailable(kind, DeclinedProblem(runtime));
        try { await EnsureTrackedAsync(kind, pin, false, token); }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException)
        {
            return RuntimeResolution.Unavailable(kind, $"{name} could not be downloaded: {error.Message}");
        }
        ForgetProbes();
        return ManagedRuntimes.FindInstalled(directory, tool, platform, workspace) is { } installed
            ? Ready(kind, installed, RuntimeSource.Downloaded)
            : RuntimeResolution.Unavailable(kind, $"{name} was downloaded but its program was not found.");
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<RuntimeKind, (long Received, long Total)> receiving = new();

    // The pinned download of a runtime, with the bytes it has received so far, which Status reports while it runs.
    private async Task EnsureTrackedAsync(RuntimeKind kind, ManagedRuntimePin pin, bool offline, CancellationToken token)
    {
        receiving[kind] = (0, 0);
        try { await ManagedRuntimes.EnsureAsync(directory, pin, managedHttp, message => CodeIntelligenceLog.Info("host.install-progress", ("server", "runtimes"), ("message", message)), offline, token, (received, total) => receiving[kind] = (received, total)); }
        finally { receiving.TryRemove(kind, out _); }
    }

    private static readonly Regex VersionPattern = new(@"\d+\.\d+\.\d+[^\s)]*", RegexOptions.Compiled);

    /// <summary>What Settings › Runtimes for tooling shows. Starts no download. The version of a program that is not ours is read by running it with <c>--version</c>.</summary>
    internal async Task<RuntimeStatus> StatusAsync(RuntimeKind kind, CancellationToken token)
    {
        var tool = ExecutableOf(kind);
        var name = NameOf(kind);
        var runtime = ManagedRuntimes.KindOf(tool)!;
        var platform = ManagedArtifacts.Platform();
        var pinned = ToolOverride(tool);
        var bytes = PluginDownloads.Size(DownloadRoot(kind)) + PluginDownloads.Size(HomeOf(ManagedRuntimes.CacheRoot(directory), kind));
        var downloaded = Directory.Exists(DownloadRoot(kind)) && ManagedRuntimes.FindInstalled(directory, tool, platform, directory) is not null;
        RuntimeSource? source = null;
        string path = "", message = "";
        if (pinned.Length > 0)
        {
            if (LanguageExecutables.Reject(pinned, directory) is { } problem) message = $"The {name} path you set cannot run. {problem}";
            else (source, path) = (RuntimeSource.Pinned, pinned);
        }
        if (source is null && Toolchain != ToolchainSource.Managed && LanguageExecutables.FindOnPath(tool, directory) is { } system) (source, path) = (RuntimeSource.System, system);
        if (source is null && downloaded && ManagedRuntimes.FindInstalled(directory, tool, platform, directory) is { } copy) (source, path) = (RuntimeSource.Downloaded, copy);
        if (source is null && message.Length == 0)
            message = RuntimesEnabled() ? $"{name} is not installed. It is downloaded the first time a plugin needs it." : OffProblem(runtime);
        var version = "";
        if (source == RuntimeSource.Downloaded) version = ManagedRuntimes.Choose(runtime.Id, platform, directory)?.Version ?? "";
        else if (path.Length > 0)
        {
            try
            {
                var run = await ChildProcess.RunAsync(path, directory, ["--version"], TimeSpan.FromSeconds(5), token, new Dictionary<string, string?> { ["UV_NO_CONFIG"] = "1" });
                if (VersionPattern.Match(run.Output) is { Success: true } match) version = match.Value;
            }
            catch (Exception error) when (error is TimeoutException or System.ComponentModel.Win32Exception or IOException) { }
        }
        var active = receiving.TryGetValue(kind, out var progress);
        return new(kind, name, source, path, version, pinned, bytes, downloaded, message)
        {
            DownloadedPath = downloaded ? ManagedRuntimes.FindInstalled(directory, tool, platform, directory) ?? "" : "",
            Downloading = active,
            ReceivedBytes = active ? progress.Received : 0,
            TotalBytes = active ? progress.Total : 0
        };
    }

    /// <summary>Downloads the pinned copy now. Fails with an <see cref="IOException"/> in plain words when offline or when the download does not verify.</summary>
    internal async Task DownloadRuntimeAsync(RuntimeKind kind, CancellationToken token)
    {
        var runtime = ManagedRuntimes.KindOf(ExecutableOf(kind))!;
        var platform = ManagedArtifacts.Platform();
        var pin = ManagedRuntimes.Choose(runtime.Id, platform, directory) ?? throw new IOException(ManagedRuntimes.NotPinned(runtime, platform, directory));
        await EnsureTrackedAsync(kind, pin, Toolchains.Offline, token);
        ForgetProbes();
    }

    /// <summary>Stops what runs from the downloaded copy and deletes the copy with its cache and tool folders. Language servers using the runtime are stopped by the caller first.</summary>
    internal void RemoveRuntimeDownload(RuntimeKind kind)
    {
        var root = DownloadRoot(kind);
        ManagedProcesses.StopUnder(root);
        PluginDownloads.Purge([root, HomeOf(ManagedRuntimes.CacheRoot(directory), kind)]);
        ForgetProbes();
    }

    /// <summary>Deletes everything a language server's plugin downloaded: its installation, its cache and its per-workspace caches.</summary>
    internal void RemoveServerDownloads(string serverId)
    {
        var paths = new List<string> { Path.Combine(directory, "servers", serverId), Path.Combine(directory, "cache", serverId) };
        var workspaces = Path.Combine(directory, "workspaces");
        if (Directory.Exists(workspaces))
            paths.AddRange(Directory.EnumerateDirectories(workspaces).Select(workspace => Path.Combine(workspace, serverId)));
        PluginDownloads.Purge(paths);
        ForgetProbes();
    }
}