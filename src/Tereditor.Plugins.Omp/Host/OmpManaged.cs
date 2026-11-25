using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Omp.Host;

/// <summary>
/// The pinned omp that tereditor downloads: the npm package at one exact version, installed with Bun into
/// <c>&lt;root&gt;/&lt;version&gt;</c> and run as <c>bun --bun &lt;cli.js&gt;</c> (the way <c>bunx --bun</c> would), never the omp on PATH. Bun's lock file
/// must record the pinned integrity of the package before the copy counts as installed. The launch lines of omp start a small program
/// named <c>omp</c> that tereditor writes (<see cref="ShimPath"/>) and that runs the copy, so a line keeps its executable word and the other
/// plugins' launch contributions still apply to it.
/// </summary>
/// <remarks>
/// The download and verification follow the Pi plugin's PiManaged on purpose; it is copied, not shared, so each agent's plugin stays independent.
/// Package facts: https://registry.npmjs.org/@oh-my-pi/pi-coding-agent/19.1.1 (the <c>omp</c> binary is <c>dist/cli.js</c>, engines <c>bun &gt;=1.4.4</c>;
/// Runtimes for tooling pins Bun 1.4.4).
/// </remarks>
internal sealed partial class OmpManaged(string root, Func<string, CancellationToken, Task<RuntimeResolution>> resolveBun)
{
    public const string Package = "@oh-my-pi/pi-coding-agent";
    public const string Version = "19.1.1";
    /// <summary>The npm registry's <c>dist.integrity</c> of <see cref="Package"/> at <see cref="Version"/>; change both together.</summary>
    public const string Integrity = "sha512-x/7xQXQ5F/ga4LTNOCSQs+cggWY1H5EF34+juTPP9QOyoFgfmOFVspXA4cBCp9EH8D967oje/HiCPpoNsWAyhw==";
    private const string Registry = "https://registry.npmjs.org/";
    private const string Marker = ".tereditor-verified";

    private readonly Lock gate = new();
    private Task? running;
    private CancellationTokenSource? cancel;
    private string progress = "";
    private string error = "";

    /// <summary><c>~/.tereditor/omp/managed</c>; checks move the home with <see cref="OmpHost.HomeVariable"/>, so they never touch the real one.</summary>
    public static string DefaultRoot() => Path.Combine(Environment.GetEnvironmentVariable(OmpHost.HomeVariable) is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "omp", "managed");

    public string Root => root;

    private string VersionDirectory => Path.Combine(root, Version);

    /// <summary>The script Bun runs.</summary>
    public string EntryPoint => Path.Combine(VersionDirectory, "node_modules", "@oh-my-pi", "pi-coding-agent", "dist", "cli.js");

    /// <summary>The folder of the <c>omp</c> program next to the managed folder (<c>~/.tereditor/omp/shim</c>).</summary>
    public string ShimDirectory => Path.Combine(Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? root, "shim");

    /// <summary>The program a launch line starts: it runs the managed omp on Bun, or says why it cannot. Forward slashes on Windows, where a line is not quoted for backslashes.</summary>
    public string ShimPath
    {
        get
        {
            var path = Path.Combine(ShimDirectory, OperatingSystem.IsWindows() ? "omp.cmd" : "omp");
            return OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        }
    }

    /// <summary>The installed version, or empty when the pinned copy is not downloaded or its record does not match the pin.</summary>
    public string InstalledVersion()
    {
        try
        {
            if (!File.Exists(EntryPoint) || File.ReadAllText(Path.Combine(VersionDirectory, Marker)).Trim() != Version + "\n" + Integrity) return "";
            using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(EntryPoint)!, "..", "package.json")));
            return package.RootElement.TryGetProperty("version", out var version) && version.GetString() == Version ? Version : "";
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

    /// <summary><paramref name="basis"/> with what the pinned copy says: its version, a download that runs and a failure.</summary>
    public OmpStatus Status(OmpStatus basis)
    {
        var installed = InstalledVersion();
        bool installing;
        string doing;
        lock (gate) (installing, doing) = (running is { IsCompleted: false }, progress);
        return basis with
        {
            PinnedVersion = Version,
            ManagedVersion = installed.Length > 0 ? installed : OlderVersion(),
            Installing = installing,
            Progress = installing ? doing : "",
            Error = LastError
        };
    }

    /// <summary>The version of a copy downloaded for an earlier pin, which an update replaces; empty when none.</summary>
    private string OlderVersion()
    {
        try { return Directory.Exists(root) ? new DirectoryInfo(root).EnumerateDirectories().FirstOrDefault(directory => !directory.Name.StartsWith('.') && directory.Name != Version)?.Name ?? "" : ""; }
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

    /// <summary>
    /// Writes the <c>omp</c> program that runs the managed copy on Bun (<c>bun --bun cli.js "$@"</c>) with Bun's environment, and a <c>omp.cmd</c> on Windows.
    /// It prints one sentence and exits 1 while the verified copy is missing, or when <paramref name="bun"/> is null (Bun is not there yet). The file is written
    /// atomically and only when its text changed, so a new pinned version or Bun path rewrites it. Returns <see cref="ShimPath"/>.
    /// </summary>
    public string WriteShim(RuntimeResolution? bun)
    {
        Directory.CreateDirectory(ShimDirectory);
        var marker = Path.Combine(VersionDirectory, Marker);
        const string Missing = "omp is not downloaded yet. Open Settings in tereditor, choose omp and press Download.";
        const string NoBun = "omp needs Bun. Turn on Runtimes for tooling in Settings, then press Download on the omp page.";
        var environment = bun?.Env.Where(pair => VariableName().IsMatch(pair.Key)).ToArray() ?? [];
        string text;
        if (OperatingSystem.IsWindows())
        {
            static string Cmd(string value) => value.Replace("%", "%%");
            var lines = new List<string> { "@echo off" };
            if (bun is { Available: true })
            {
                lines.Add($"if not exist \"{EntryPoint}\" goto missing");
                lines.Add($"if not exist \"{marker}\" goto missing");
                lines.AddRange(environment.Select(pair => $"set \"{pair.Key}={Cmd(pair.Value)}\""));
                lines.Add($"\"{bun.Command}\" --bun \"{EntryPoint}\" %*");
                lines.Add("exit /b %errorlevel%");
                lines.Add(":missing");
                lines.Add($"echo {Missing} 1>&2");
            }
            else lines.Add($"echo {NoBun} 1>&2");
            lines.Add("exit /b 1");
            text = string.Join("\r\n", lines) + "\r\n";
        }
        else
        {
            static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
            text = bun is { Available: true }
                ? "#!/bin/sh\n# Written by tereditor: runs the pinned omp on Bun.\n" +
                  $"if [ ! -f {Quote(EntryPoint)} ] || [ ! -f {Quote(marker)} ]; then echo {Quote(Missing)} >&2; exit 1; fi\n" +
                  string.Concat(environment.Select(pair => $"export {pair.Key}={Quote(pair.Value)}\n")) +
                  $"exec {Quote(bun.Command)} --bun {Quote(EntryPoint)} \"$@\"\n"
                : $"#!/bin/sh\n# Written by tereditor: runs the pinned omp on Bun.\necho {Quote(NoBun)} >&2\nexit 1\n";
        }
        var path = Path.Combine(ShimDirectory, OperatingSystem.IsWindows() ? "omp.cmd" : "omp");
        if (File.Exists(path) && File.ReadAllText(path) == text) return ShimPath;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(temporary, path, true);
        return ShimPath;
    }

    public void Cancel()
    {
        lock (gate) cancel?.Cancel();
    }

    public void Remove()
    {
        Cancel();
        PluginDownloads.Purge([root, ShimDirectory]);
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
            File.WriteAllText(Path.Combine(temporary, "package.json"), "{\"name\":\"tereditor-omp\",\"private\":true,\"dependencies\":{\"" + Package + "\":\"" + Version + "\"}}");
            Say($"Downloading omp {Version}…");
            await RunBunAsync(bun, temporary, token);
            Say("Checking omp…");
            var entry = Path.Combine(temporary, "node_modules", "@oh-my-pi", "pi-coding-agent", "dist", "cli.js");
            var lockFile = Path.Combine(temporary, "bun.lock");
            if (!File.Exists(lockFile)) throw new InvalidOperationException("Bun did not write a lock file, so the download could not be checked. Update Bun.");
            if (!File.ReadAllText(lockFile).Contains(Integrity, StringComparison.Ordinal)) throw new InvalidOperationException($"The download of omp {Version} does not match the pinned checksum. It was deleted.");
            if (!File.Exists(entry)) throw new InvalidOperationException("The download has no omp program.");
            File.WriteAllText(Path.Combine(temporary, Marker), Version + "\n" + Integrity);
            if (Directory.Exists(VersionDirectory)) PluginDownloads.Purge([VersionDirectory]);
            Directory.Move(temporary, VersionDirectory);
            temporary = null;
            foreach (var other in new DirectoryInfo(root).EnumerateDirectories().Where(directory => directory.Name != Version))
                PluginDownloads.Purge([other.FullName]);
            WriteShim(bun);
        }
        catch (OperationCanceledException)
        {
            lock (gate) error = "The download was canceled.";
            throw;
        }
        catch (Exception problem) when (problem is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            lock (gate) error = "omp could not be downloaded: " + problem.Message;
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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();
}