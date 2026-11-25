using System.Diagnostics;
using System.Text.Json;

using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>
/// The pinned Pi that tereditor downloads: the npm package at one exact version, installed with Bun into
/// <c>&lt;root&gt;/&lt;version&gt;</c> and run as <c>bun --bun &lt;cli.js&gt;</c> (the way <c>bunx --bun</c> would), never the Pi on PATH.
/// Bun's lock file must record the pinned integrity of the package before the copy counts as installed.
/// </summary>
internal sealed class PiManaged(string root, Func<string, CancellationToken, Task<RuntimeResolution>> resolveBun, Func<string, CancellationToken, Task<RuntimeResolution>> findBun)
{
    public const string Package = "@earendil-works/pi-coding-agent";
    public const string Version = "1.6.0";
    /// <summary>The npm registry's <c>dist.integrity</c> of <see cref="Package"/> at <see cref="Version"/>; change both together.</summary>
    public const string Integrity = "sha512-zX1BVEsshx7A2MRuQ6jfr3RJGWw4Wn+vLq5ZfmlvS1RskhRmByaSZVXH/zcwRrWHO7qPgs98pNCbPOEti/t0fQ==";
    private const string Registry = "https://registry.npmjs.org/";
    public const string NotDownloaded = "Pi is not downloaded yet. Open Settings, choose Pi and press Download.";
    public const string NoBun = "Pi needs Bun. Open Settings, choose Runtimes and press Download, or install Bun yourself.";
    private const string Marker = ".tereditor-verified";

    private readonly Lock gate = new();
    private Task? running;
    private CancellationTokenSource? cancel;
    private string progress = "";
    private string error = "";

    public static string DefaultRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "pi", "managed");

    public string Root => root;

    private string VersionDirectory => Path.Combine(root, Version);

    /// <summary>The script Bun runs.</summary>
    public string EntryPoint => Path.Combine(VersionDirectory, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");

    /// <summary>The installed version, or empty when the pinned copy is not downloaded or its record does not match the pin.</summary>
    public string InstalledVersion()
    {
        try
        {
            if (!File.Exists(EntryPoint) || File.ReadAllText(Path.Combine(VersionDirectory, Marker)).Trim() != Version + "\n" + Integrity) return "";
            using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(EntryPoint)!, "..", "..", "package.json")));
            return package.RootElement.TryGetProperty("version", out var version) && version.GetString() == Version ? Version : "";
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException) { return ""; }
    }

    /// <summary>What the last download failed with; empty when it did not fail or one runs.</summary>
    public string LastError
    {
        get { lock (gate) return running is { IsCompleted: false } ? "" : error; }
    }

    /// <summary>The status of the pinned copy; with <paramref name="source"/> managed it is the whole answer, otherwise it is added to the system Pi's status.</summary>
    public PiStatus Status(string source, PiStatus? system = null)
    {
        var installed = InstalledVersion();
        var found = installed.Length > 0 ? installed : OlderVersion();
        var basis = source == PiSources.Managed
            ? new PiStatus(installed.Length > 0, "managed") { Version = installed.Length > 0 ? installed : null, Detail = installed.Length > 0 ? null : "The pinned Pi is not downloaded yet." }
            : system!;
        bool installing;
        string doing;
        lock (gate) (installing, doing) = (running is { IsCompleted: false }, progress);
        return basis with { Source = source, PinnedVersion = Version, ManagedVersion = found, Installing = installing, Progress = installing ? doing : "", Error = LastError };
    }

    /// <summary>The version of a copy downloaded for an earlier pin, which an update replaces; empty when none.</summary>
    private string OlderVersion()
    {
        try { return Directory.Exists(root) ? new DirectoryInfo(root).EnumerateDirectories().FirstOrDefault(directory => !directory.Name.StartsWith('.') && directory.Name != Version)?.Name ?? "" : ""; }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { return ""; }
    }

    public bool Running
    {
        get { lock (gate) return running is { IsCompleted: false }; }
    }

    /// <summary>Starts the download unless one runs, and waits for it. The caller's token only stops the waiting; <see cref="Cancel"/> stops the download.</summary>
    public async Task EnsureAsync(string workspace, CancellationToken token)
    {
        if (InstalledVersion().Length > 0) return;
        Task job;
        lock (gate)
        {
            if (running is not { IsCompleted: false })
            {
                cancel?.Dispose();
                cancel = new CancellationTokenSource();
                error = "";
                progress = "Looking for Bun…";
                var source = cancel.Token;
                running = Task.Run(() => InstallAsync(workspace, source), CancellationToken.None);
            }
            job = running;
        }
        await job.WaitAsync(token);
    }

    /// <summary>
    /// Returns <paramref name="spawn"/> as the command that runs the pinned Pi on Bun (<c>bun --bun cli.js --mode rpc …</c>). It never downloads Pi or Bun:
    /// a missing copy or a missing Bun fails with a sentence that points to Settings. A blocked spawn is returned as it is, so it fails with its reason.
    /// </summary>
    /// <param name="track">Told which process started from which Bun, so turning Runtimes for tooling off stops it.</param>
    public async Task<PiSpawn> PrepareAsync(PiSpawn spawn, Action<RuntimeResolution, int> track)
    {
        if (spawn.Blocked is not null) return spawn;
        if (InstalledVersion().Length == 0) throw new InvalidOperationException(NotDownloaded);
        var bun = await findBun(spawn.Cwd, CancellationToken.None);
        if (!bun.Available) throw new InvalidOperationException(bun.Message);
        var environment = new Dictionary<string, string>(spawn.Environment);
        foreach (var (name, value) in bun.Env) environment[name] = value;
        return spawn with { Command = bun.Command, Prefix = ["--bun", EntryPoint], Environment = environment, Started = id => track(bun, id) };
    }

    public void Cancel()
    {
        lock (gate) cancel?.Cancel();
    }

    public void Remove()
    {
        Cancel();
        PluginDownloads.Purge([root]);
        lock (gate) error = "";
    }

    private void Say(string text)
    {
        lock (gate) progress = text;
    }

    private async Task InstallAsync(string workspace, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(root);
            var bun = await resolveBun(workspace.Length > 0 ? workspace : root, token);
            if (!bun.Available) throw new InvalidOperationException(bun.Message);
            temporary = Path.Combine(root, ".installing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            File.WriteAllText(Path.Combine(temporary, "package.json"), "{\"name\":\"tereditor-pi\",\"private\":true,\"dependencies\":{\"" + Package + "\":\"" + Version + "\"}}");
            Say($"Downloading Pi {Version}…");
            await RunBunAsync(bun, temporary, token);
            Say("Checking Pi…");
            var entry = Path.Combine(temporary, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");
            var lockFile = Path.Combine(temporary, "bun.lock");
            if (!File.Exists(lockFile)) throw new InvalidOperationException("Bun did not write a lock file, so the download could not be checked. Update Bun.");
            if (!File.ReadAllText(lockFile).Contains(Integrity, StringComparison.Ordinal)) throw new InvalidOperationException($"The download of Pi {Version} does not match the pinned checksum. It was deleted.");
            if (!File.Exists(entry)) throw new InvalidOperationException("The download has no Pi program.");
            File.WriteAllText(Path.Combine(temporary, Marker), Version + "\n" + Integrity);
            if (Directory.Exists(VersionDirectory)) PluginDownloads.Purge([VersionDirectory]);
            Directory.Move(temporary, VersionDirectory);
            temporary = null;
            foreach (var other in new DirectoryInfo(root).EnumerateDirectories().Where(directory => directory.Name != Version))
                PluginDownloads.Purge([other.FullName]);
        }
        catch (OperationCanceledException)
        {
            lock (gate) error = "The download was canceled.";
            throw;
        }
        catch (Exception problem) when (problem is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            lock (gate) error = "Pi could not be downloaded: " + problem.Message;
            throw;
        }
        finally
        {
            if (temporary is not null) { try { PluginDownloads.Purge([temporary]); } catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { } }
            Say("");
        }
    }

    private static async Task RunBunAsync(RuntimeResolution bun, string directory, CancellationToken token)
    {
        // The package's own install scripts never run, and a registry setting in the user's files cannot send the download elsewhere.
        var start = new ProcessStartInfo(bun.Command, ["install", "--ignore-scripts", "--no-progress", "--registry=" + Registry])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var (name, value) in bun.Env) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Bun could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromMinutes(10), token);
        }
        catch (Exception problem) when (problem is OperationCanceledException or TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (problem is TimeoutException) throw new InvalidOperationException("Bun took too long.");
            throw;
        }
        await Task.WhenAll(output, errors);
        if (process.ExitCode != 0)
        {
            var lines = (errors.Result + "\n" + output.Result).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            throw new InvalidOperationException(lines.Length > 0 ? lines[^1] : $"Bun exited with code {process.ExitCode}.");
        }
    }
}