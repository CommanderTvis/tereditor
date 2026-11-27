using System.Diagnostics;
using System.Text.Json;

using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Agent.Host;

/// <summary>An agent's npm package at one exact version.</summary>
/// <param name="Name">The agent's name in messages.</param>
/// <param name="Package">The npm package.</param>
/// <param name="Version">The exact version.</param>
/// <param name="Integrity">The npm registry's <c>dist.integrity</c> of that version.</param>
/// <param name="Entry">The script Bun runs, as a path inside the package with forward slashes.</param>
internal sealed record PinnedPackage(string Name, string Package, string Version, string Integrity, string Entry);

/// <summary>What the pinned copy says now. <paramref name="ManagedVersion"/> is the pinned copy's version or an earlier pin's.</summary>
internal sealed record ManagedState(string InstalledVersion, string ManagedVersion, bool Installing, string Progress, string Error);

/// <summary>
/// A pinned agent that tereditor downloads: the npm package at one exact version, installed with Bun into
/// <c>&lt;root&gt;/&lt;version&gt;</c> and run as <c>bun --bun &lt;entry&gt;</c> (the way <c>bunx --bun</c> would), never the one on PATH.
/// Bun's lock file must record the pinned integrity of the package before the copy counts as installed.
/// Only <see cref="EnsureAsync"/> downloads, and only a plugin's Download request may call it.
/// </summary>
internal class ManagedBunPackage(PinnedPackage pin, string root, Func<string, CancellationToken, Task<RuntimeResolution>> resolveBun)
{
    private const string Registry = "https://registry.npmjs.org/";
    private const string Marker = ".tereditor-verified";

    private readonly Lock gate = new();
    private Task? running;
    private CancellationTokenSource? cancel;
    private string progress = "";
    private string error = "";

    public string Name => pin.Name;

    public string Root => root;

    private string VersionDirectory => Path.Combine(root, pin.Version);

    /// <summary>The record of a checked download; a copy without it is not installed.</summary>
    protected string MarkerPath => Path.Combine(VersionDirectory, Marker);

    private string PackageDirectory(string install) => Path.Combine([install, "node_modules", .. pin.Package.Split('/')]);

    private string Script(string install) => Path.Combine([PackageDirectory(install), .. pin.Entry.Split('/')]);

    /// <summary>The script Bun runs.</summary>
    public string EntryPoint => Script(VersionDirectory);

    /// <summary>The installed version, or empty when the pinned copy is not downloaded or its record does not match the pin.</summary>
    public string InstalledVersion()
    {
        try
        {
            if (!File.Exists(EntryPoint) || File.ReadAllText(MarkerPath).Trim() != pin.Version + "\n" + pin.Integrity) return "";
            using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackageDirectory(VersionDirectory), "package.json")));
            return package.RootElement.TryGetProperty("version", out var version) && version.GetString() == pin.Version ? pin.Version : "";
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException) { return ""; }
    }

    /// <summary>What the last download failed with; empty when it did not fail or one runs.</summary>
    public string LastError
    {
        get { lock (gate) return running is { IsCompleted: false } ? "" : error; }
    }

    public bool Running
    {
        get { lock (gate) return running is { IsCompleted: false }; }
    }

    /// <summary>The pinned copy's version, a download that runs and a failure.</summary>
    public ManagedState State()
    {
        var installed = InstalledVersion();
        var found = installed.Length > 0 ? installed : OlderVersion();
        bool installing;
        string doing;
        lock (gate) (installing, doing) = (running is { IsCompleted: false }, progress);
        return new(installed, found, installing, installing ? doing : "", LastError);
    }

    /// <summary>The version of a copy downloaded for an earlier pin, which an update replaces; empty when none.</summary>
    private string OlderVersion()
    {
        try { return Directory.Exists(root) ? new DirectoryInfo(root).EnumerateDirectories().FirstOrDefault(directory => !directory.Name.StartsWith('.') && directory.Name != pin.Version)?.Name ?? "" : ""; }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { return ""; }
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

    public void Cancel()
    {
        lock (gate) cancel?.Cancel();
    }

    /// <summary>Deletes everything the download made.</summary>
    public void Remove()
    {
        Cancel();
        PluginDownloads.Purge(Downloads);
        lock (gate) error = "";
    }

    /// <summary>The folders <see cref="Remove"/> deletes.</summary>
    protected virtual IEnumerable<string> Downloads => [root];

    /// <summary>Called once a checked download is in place.</summary>
    protected virtual void Installed(RuntimeResolution bun) { }

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
            File.WriteAllText(Path.Combine(temporary, "package.json"), "{\"name\":\"tereditor-" + pin.Name.ToLowerInvariant() + "\",\"private\":true,\"dependencies\":{\"" + pin.Package + "\":\"" + pin.Version + "\"}}");
            Say($"Downloading {pin.Name} {pin.Version}…");
            await RunBunAsync(bun, temporary, token);
            Say($"Checking {pin.Name}…");
            var lockFile = Path.Combine(temporary, "bun.lock");
            if (!File.Exists(lockFile)) throw new InvalidOperationException("Bun did not write a lock file, so the download could not be checked. Update Bun.");
            if (!File.ReadAllText(lockFile).Contains(pin.Integrity, StringComparison.Ordinal)) throw new InvalidOperationException($"The download of {pin.Name} {pin.Version} does not match the pinned checksum. It was deleted.");
            if (!File.Exists(Script(temporary))) throw new InvalidOperationException($"The download has no {pin.Name} program.");
            File.WriteAllText(Path.Combine(temporary, Marker), pin.Version + "\n" + pin.Integrity);
            if (Directory.Exists(VersionDirectory)) PluginDownloads.Purge([VersionDirectory]);
            Directory.Move(temporary, VersionDirectory);
            temporary = null;
            foreach (var other in new DirectoryInfo(root).EnumerateDirectories().Where(directory => directory.Name != pin.Version))
                PluginDownloads.Purge([other.FullName]);
            Installed(bun);
        }
        catch (OperationCanceledException)
        {
            lock (gate) error = "The download was canceled.";
            throw;
        }
        catch (Exception problem) when (problem is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            lock (gate) error = pin.Name + " could not be downloaded: " + problem.Message;
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