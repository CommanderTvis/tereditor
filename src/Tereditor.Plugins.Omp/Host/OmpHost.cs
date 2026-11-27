using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Tereditor.Plugins.Agent.Host;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Omp.Host;

/// <summary>
/// The omp plugin's host half: the version probe, the tereditor profile's MCP file, the per-launch session folders,
/// the context-file resolution, the process poll that writes the agent record, and the revive prefill.
/// </summary>
public sealed partial class OmpHost : PluginHostModule
{
    public override PluginContract Contract => OmpContract.Contract;

    /// <summary>The variable that overrides the home directory omp's files are resolved under, for checks.</summary>
    public const string HomeVariable = "TEREDITOR_OMP_HOME";

    private static string HomeDirectory => Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The profile's <c>mcp.json</c>: tereditor's server only, its URL taken from each terminal's environment.</summary>
    public static string ProfileMcpPath(string home) => Path.Combine(OmpContext.UserDirectory(home, true), "mcp.json");

    /// <summary>
    /// Writes the profile's MCP file atomically. The file is tereditor's own, so it is replaced whole; omp expands
    /// <c>${VAR}</c> when it loads it (https://omp.sh/docs/mcp), so one file serves every terminal's own MCP URL.
    /// </summary>
    public static void WriteProfileMcp(string home)
    {
        var path = ProfileMcpPath(home);
        var text = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["tereditor"] = new JsonObject { ["type"] = "http", ["url"] = "${THINKRAIL_MCP_URL}" }
            }
        }.ToJsonString(new() { WriteIndented = true }) + "\n";
        OwnedFile.Write(path, text);
    }

    /// <summary>The newest session file in a launch's session folder, which a revive resumes; null when it has none.</summary>
    public static string? NewestSession(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateFiles("*.jsonl", SearchOption.AllDirectories).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task<OmpStatus> ReadStatusAsync(string command, string home, bool profile)
    {
        var executable = OmpLaunch.Executable(command);
        var status = new OmpStatus(false, executable) { ProfileMcpPath = profile ? ProfileMcpPath(home) : null };
        var probe = await AgentVersion.ProbeAsync(executable, "omp");
        return status with { Installed = probe.Installed, Usable = probe.Installed, Version = probe.Version, Detail = probe.Detail };
    }

    /// <summary>
    /// The status of the omp a launch would run: the managed copy (downloaded, or downloadable while Bun is available) or the system command, whichever
    /// <see cref="OmpSettings.Source"/> and <paramref name="bunAvailable"/> pick. The managed one runs no process: its version is the pinned one on disk.
    /// </summary>
    internal static async Task<OmpStatus> ComposeStatusAsync(OmpSettings settings, string home, OmpManaged managed, bool bunAvailable)
    {
        if (OmpSources.Effective(settings.Source, bunAvailable) == OmpSources.Managed)
        {
            var installed = managed.InstalledVersion();
            return managed.Status(new OmpStatus(installed.Length > 0, managed.ShimPath)
            {
                Source = OmpSources.Managed,
                Version = installed.Length > 0 ? installed : null,
                Usable = installed.Length > 0 || bunAvailable,
                LaunchCommand = managed.ShimPath,
                ProfileMcpPath = settings.Mcp ? ProfileMcpPath(home) : null,
                Detail = installed.Length > 0 ? null
                    : bunAvailable ? "The pinned omp is not downloaded yet."
                    : "Bun is not available. Turn on Runtimes for tooling in Settings, or choose the system command."
            });
        }
        var system = await ReadStatusAsync(settings.Command, home, settings.Mcp);
        return managed.Status(system) with
        {
            Source = OmpSources.System,
            SourceNote = settings.Source.Length == 0 && !bunAvailable ? "Bun from Runtimes for tooling is not available, so omp runs from your system command." : ""
        };
    }

    private static ProcessStartInfo OmpProcess(string executable, IEnumerable<string> arguments, string? home)
    {
        var start = new ProcessStartInfo(executable, arguments) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        // The profile is chosen by the arguments alone; an inherited OMP_PROFILE would redirect the export.
        start.Environment.Remove("OMP_PROFILE");
        if (home is not null)
        {
            start.Environment["HOME"] = home;
            start.Environment["USERPROFILE"] = home;
        }
        return start;
    }

    /// <summary>
    /// Copies the default profile's stored credentials into tereditor's profile: <c>omp auth export</c> piped straight
    /// into <c>omp --profile tereditor auth import -</c> (syntax from <c>omp auth --help</c>, omp 18.12.6). The
    /// credentials stream process to process: tereditor never writes, logs or returns them. Error output is reduced to
    /// a short line with long tokens masked. <paramref name="home"/> overrides omp's home directory (checks only).
    /// </summary>
    internal static async Task<OmpSignInResult> ImportSignInAsync(string command, string? home, CancellationToken cancellationToken)
    {
        var executable = OmpLaunch.Executable(command);
        Process? export = null, import = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var exportStart = OmpProcess(executable, ["auth", "export"], home);
            exportStart.RedirectStandardOutput = true;
            var importStart = OmpProcess(executable, ["--profile", OmpLaunch.Profile, "auth", "import", "-"], home);
            importStart.RedirectStandardInput = true;
            importStart.RedirectStandardOutput = true;
            export = Process.Start(exportStart);
            if (export is null) return new(false, "omp could not be started.");
            import = Process.Start(importStart);
            if (import is null) return new(false, "omp could not be started.");
            var exportErrors = export.StandardError.ReadToEndAsync(timeout.Token);
            var importErrors = import.StandardError.ReadToEndAsync(timeout.Token);
            var importOutput = import.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await export.StandardOutput.BaseStream.CopyToAsync(import.StandardInput.BaseStream, timeout.Token);
            }
            catch (IOException) { } // The import exited early; its exit code says why.
            finally
            {
                try { import.StandardInput.Close(); } catch (IOException) { }
            }
            await export.WaitForExitAsync(timeout.Token);
            await import.WaitForExitAsync(timeout.Token);
            await importOutput;
            if (export.ExitCode != 0) return new(false, $"omp auth export failed (exit {export.ExitCode}){Sanitize(await exportErrors)}");
            if (import.ExitCode != 0) return new(false, $"omp auth import failed (exit {import.ExitCode}){Sanitize(await importErrors)}");
            return new(true, $"Your omp sign-in now works in tereditor's \"{OmpLaunch.Profile}\" profile.");
        }
        catch (OperationCanceledException) { return new(false, cancellationToken.IsCancellationRequested ? "Canceled." : "omp auth timed out."); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(false, $"{executable} could not be run.");
        }
        finally
        {
            foreach (var process in new[] { export, import })
            {
                if (process is null) continue;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// A fresh profile shows omp's first-run setup wizard even with provider keys exported. When the profile's
    /// <c>startup.setupWizard</c> is still the default <c>true</c> and <c>setupVersion</c> is 0 (the wizard never
    /// completed), sets it to <c>false</c> through <c>omp --profile tereditor config get|set</c>. Anything else, including
    /// a user who re-enabled it or finished the wizard, is left alone. One 10 s bound covers all calls; never throws.
    /// Returns a short trace line: "set", "unchanged: ..." or "failed: ...". <paramref name="home"/> overrides omp's home (checks only).
    /// </summary>
    internal static async Task<string> SkipSetupWizardAsync(string command, string? home)
    {
        var executable = OmpLaunch.Executable(command);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<string?> Config(params string[] arguments)
        {
            var start = OmpProcess(executable, ["--profile", OmpLaunch.Profile, "config", .. arguments], home);
            start.RedirectStandardOutput = true;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("omp could not be started.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
                var errors = process.StandardError.ReadToEndAsync(cancel.Token);
                await process.WaitForExitAsync(cancel.Token);
                await errors;
                return process.ExitCode == 0 ? (await output).Trim() : null;
            }
            finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        }
        try
        {
            if (await Config("get", "startup.setupWizard") != "true") return "unchanged: setup wizard is not at its default";
            if (await Config("get", "setupVersion") != "0") return "unchanged: setup wizard already completed or unreadable";
            return await Config("set", "startup.setupWizard", "false") is null ? "failed: omp config set exited non-zero" : "set";
        }
        catch (OperationCanceledException) { return "failed: omp config timed out"; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return "failed: " + error.Message; }
    }

    /// <summary>The first non-empty error line, at most 160 characters, with anything token-like masked.</summary>
    internal static string Sanitize(string errors)
    {
        var line = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null) return ".";
        line = TokenLike().Replace(line, "***");
        return ": " + (line.Length > 160 ? line[..160] + "…" : line);
    }

    [GeneratedRegex(@"[A-Za-z0-9_\-.+/=]{20,}")]
    private static partial Regex TokenLike();

    public override async ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var settings = () => context.Settings<OmpSettings>();
        var sessions = context.StateDirectory is { } state ? Path.Combine(state, "sessions") : Path.Combine(Path.GetTempPath(), "tereditor-omp-sessions");
        var managed = new OmpManaged(OmpManaged.DefaultRoot(), (workspace, token) => context.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, token).AsTask());
        // Whether launches run the managed copy: set by every status read and settings change; the system command until the first one.
        var managedActive = false;
        string Command() => managedActive ? managed.ShimPath : settings().Command;

        async ValueTask<string> RootOf(string workspaceId, CancellationToken cancellationToken) =>
            (await context.WorkspaceAsync(workspaceId, cancellationToken))?.Path ?? throw new InvalidOperationException($"Unknown workspace: {workspaceId}");

        // omp starts in the tereditor profile when MCP is on, or when a model source contributes the profile to its launch.
        bool UsesSource() => context.AgentLaunchContributions(OmpManifest.Id).Any(contribution => contribution.Arguments.Contains("--profile"));
        bool UsesProfile() => settings().Mcp || UsesSource();

        // Bun is there (a pinned path, one on PATH or the downloaded copy) or the Runtimes plugin would download it. Starts no download.
        async Task<(bool Present, bool Available)> BunAsync(CancellationToken cancellationToken)
        {
            try
            {
                var bun = (await context.RuntimeStatusAsync(cancellationToken)).FirstOrDefault(status => status.Kind == RuntimeKind.Bun);
                return (bun is { Source: not null }, bun is { Source: not null } or { Downloadable: true });
            }
            catch (Exception error) when (error is not OperationCanceledException) { return (false, false); }
        }

        // Settles which source launches use and keeps the program they start in step with Bun's current path; never downloads.
        async Task<bool> RefreshAsync(CancellationToken cancellationToken)
        {
            var (present, available) = await BunAsync(cancellationToken);
            managedActive = OmpSources.Effective(settings().Source, available) == OmpSources.Managed;
            if (managedActive)
            {
                try
                {
                    var bun = present ? await context.ResolveRuntimeAsync(RuntimeKind.Bun, managed.Root, cancellationToken) : null;
                    managed.WriteShim(bun is { Available: true } ? bun : null);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { context.Log.Warn("omp program was not written: " + error.Message); }
            }
            return available;
        }

        async Task<OmpStatus> StatusAsync(CancellationToken cancellationToken)
        {
            var available = await RefreshAsync(cancellationToken);
            return await ComposeStatusAsync(settings(), HomeDirectory, managed, available);
        }

        string? wizardPrepared = null;
        void EnsureProfile()
        {
            var current = settings();
            if (!current.Mcp && !UsesSource()) return;
            if (current.Mcp)
            {
                try { WriteProfileMcp(HomeDirectory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { context.Log.Warn("omp profile MCP file was not written: " + error.Message); }
            }
            // Once per command per host run, in the background so a launch never waits on it. The managed omp waits until it is downloaded
            // (a download calls this again), so the first try does not fail on a program that is not there yet.
            var command = Command();
            if (managedActive && managed.InstalledVersion().Length == 0) return;
            if (Interlocked.Exchange(ref wizardPrepared, command) == command) return;
            var home = Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } overridden ? overridden : null;
            _ = Task.Run(async () => context.Log.Info("omp setup wizard: " + await SkipSetupWizardAsync(command, home)));
        }

        context.Method(OmpContract.Status, async (_, _, token) => await Task.Run(() => StatusAsync(token), token));
        // A finished download prepares the profile, which waits for the program.
        ManagedDownloads.Register(context, managed, OmpContract.Install, OmpContract.CancelInstall, OmpContract.Remove, token => Task.Run(() => StatusAsync(token), token), EnsureProfile);
        context.Method(OmpContract.Context, async (p, _, ct) =>
        {
            var root = await RootOf(p.WorkspaceId, ct);
            var profile = UsesProfile();
            return await Task.Run(() => OmpContext.Resolve(root, HomeDirectory, profile), ct);
        });
        context.Method(OmpContract.CreateContextFile, async (p, _, ct) =>
        {
            var root = await RootOf(p.WorkspaceId, ct);
            var profile = UsesProfile();
            return await Task.Run(() =>
            {
                OmpContext.Create(root, p.Target);
                return OmpContext.Resolve(root, HomeDirectory, profile);
            }, ct);
        });

        var importing = new SemaphoreSlim(1, 1);
        // Only ever on an explicit request; host calls come from authenticated clients only.
        context.Method(OmpContract.ImportSignIn, async (_, _, ct) =>
        {
            if (!await importing.WaitAsync(0, ct)) return new OmpSignInResult(false, "An import is already running.");
            try
            {
                await RefreshAsync(ct);
                if (managedActive && managed.InstalledVersion().Length == 0) return new OmpSignInResult(false, "omp is not downloaded yet. Download it in Settings, then try again.");
                var command = Command();
                var home = Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } overridden ? overridden : null;
                return await Task.Run(() => ImportSignInAsync(command, home, ct), ct);
            }
            finally { importing.Release(); }
        });

        context.TerminalEnvironment(_ =>
        {
            EnsureProfile();
            return new Dictionary<string, string> { [OmpLaunch.SessionsVariable] = sessions };
        });
        // A change of source settles which program launches use first, then the profile is prepared for it.
        context.OnSettings<OmpSettings>(changed =>
        {
            EnsureProfile();
            _ = Task.Run(async () =>
            {
                await RefreshAsync(CancellationToken.None);
                EnsureProfile();
            });
        });
        await Task.Run(EnsureProfile);
        _ = Task.Run(async () =>
        {
            await RefreshAsync(CancellationToken.None);
            EnsureProfile();
        });

        var watch = new AgentWatch(
            () => [.. context.Terminals().Where(item => item.Pid is not null).Select(item => new AgentWatchTarget(item.Terminal.WorkspaceId, item.Terminal.TabKey, item.Pid!.Value))],
            (workspaceId, tabKey, pid) =>
            {
                var terminal = new TerminalRef(workspaceId, tabKey);
                var existing = context.AgentRecord(terminal);
                if (existing is not null && existing.Kind != OmpManifest.Id) return;
                var arguments = ProcessSnapshot.CaptureCommand(pid) ?? "";
                context.SetAgentRecord(terminal, new(OmpManifest.Id, existing?.Command ?? OmpLaunch.Executable(settings().Command))
                {
                    SessionId = OmpLaunch.SessionIdOf(arguments) ?? existing?.SessionId,
                    Model = existing?.Model,
                    LaunchedByUi = ProcessSnapshot.CaptureUiLaunch(pid)
                });
            },
            (workspaceId, tabKey) =>
            {
                var terminal = new TerminalRef(workspaceId, tabKey);
                if (context.AgentRecord(terminal)?.Kind == OmpManifest.Id) context.SetAgentRecord(terminal, null);
            },
            // The managed omp is a Bun process running the managed copy's script.
            isManaged: pid => managedActive && ProcessSnapshot.CaptureCommand(pid)?.Contains(managed.EntryPoint, StringComparison.Ordinal) == true);
        context.OnTerminal(change =>
        {
            switch (change)
            {
                case TerminalSpawned: watch.Poke(); break;
                case TerminalClosed closed: watch.Forget(closed.Terminal.WorkspaceId, closed.Terminal.TabKey); break;
            }
        });

        // A revived omp tab resumes its own session folder's newest session, or continues in it when none was saved yet.
        context.RevivePrefill((_, record) =>
        {
            if (record.Kind != OmpManifest.Id) return null;
            var current = settings();
            var directory = record.SessionId is { Length: > 0 } id ? Path.Combine(sessions, id) : null;
            var newest = directory is null ? null : NewestSession(directory);
            // Start fresh keeps the tab's session directory but resumes nothing in it.
            string Line(bool fresh) => OmpLaunch.Line(managedActive ? OmpLaunch.ManagedExecutable(managed.ShimPath) : current.Command, new()
            {
                Mcp = current.Mcp,
                Windows = OperatingSystem.IsWindows(),
                UiLaunch = true,
                Model = current.Model,
                ApprovalMode = current.ApprovalMode,
                Advisor = current.Advisor,
                OwnCodeIntelligence = current.OwnCodeIntelligence,
                SessionDirectory = directory,
                ResumePath = fresh ? null : newest,
                Continue = !fresh && newest is null && directory is null,
                Contributions = context.AgentLaunchContributions(OmpManifest.Id)
            });
            return new RevivePrefill(Line(false)) { Program = true, Fresh = Line(true) };
        });

        return () =>
        {
            watch.Dispose();
            return ValueTask.CompletedTask;
        };
    }
}