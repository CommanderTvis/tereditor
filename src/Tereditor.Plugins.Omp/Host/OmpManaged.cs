using System.Text.RegularExpressions;

using Tereditor.Plugins.Agent.Host;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Omp.Host;

/// <summary>
/// The pinned omp that tereditor downloads (<see cref="ManagedBunPackage"/>). The launch lines of omp start a small program
/// named <c>omp</c> that tereditor writes (<see cref="ShimPath"/>) and that runs the copy, so a line keeps its executable word and the other
/// plugins' launch contributions still apply to it. The program also keeps the copy from updating itself: only tereditor's Update button changes the version.
/// </summary>
/// <remarks>
/// Package facts: https://registry.npmjs.org/@oh-my-pi/pi-coding-agent/19.2.1 (the <c>omp</c> binary is <c>dist/cli.js</c>, engines <c>bun &gt;=1.4.4</c>;
/// Runtimes for tooling pins Bun 1.4.4).
/// </remarks>
internal sealed partial class OmpManaged(string root, Func<string, CancellationToken, Task<RuntimeResolution>> resolveBun)
    : ManagedBunPackage(new("omp", Package, Version, Integrity, "dist/cli.js"), root, resolveBun)
{
    public const string Package = "@oh-my-pi/pi-coding-agent";
    public const string Version = "19.2.1";
    /// <summary>The npm registry's <c>dist.integrity</c> of <see cref="Package"/> at <see cref="Version"/>; change both together.</summary>
    public const string Integrity = "sha512-9bDPzpbdwNhsW/PoLwdATmZltxxt50ab9uh0gTTXIlLJkajdwGhJwgdVCZE/WCRMadG0cVN2EkdOuKnhQUShRg==";

    /// <summary>
    /// The settings omp reads for the managed copy's runs only, named to it in <c>PI_CONFIG_FILES</c>, the variable omp's settings docs give wrappers
    /// (<c>docs/settings.md</c> in the package). It ranks above the user's global and project settings: no update check, no "Update Available" notice, no
    /// background update.
    /// </summary>
    internal const string Settings = "# Written by tereditor for the pinned omp. Update it from the omp page in tereditor.\nstartup:\n  checkUpdate: false\nupdate:\n  auto: false\n";

    /// <summary>
    /// Says that tereditor keeps this install, so <c>omp update</c> refuses to change it. omp reads <c>manager.json</c> two folders above the
    /// <c>omp</c> it finds on PATH (<c>src/cli/managed-install.ts</c> in the package; not in its docs).
    /// </summary>
    internal const string Manager = "{\"manager\":\"tereditor\",\"name\":\"tereditor\"}\n";

    /// <summary><c>~/.tereditor/omp/managed</c>; checks move the home with <see cref="OmpHost.HomeVariable"/>, so they never touch the real one.</summary>
    public static string DefaultRoot() => Path.Combine(Environment.GetEnvironmentVariable(OmpHost.HomeVariable) is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "omp", "managed");

    /// <summary>The folder next to the managed one (<c>~/.tereditor/omp/shim</c>) that holds the <c>omp</c> program in <c>bin</c> and its two files.</summary>
    public string ShimDirectory => Path.Combine(Path.GetDirectoryName(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? Root, "shim");

    /// <summary>The program a launch line starts: it runs the managed omp on Bun, or says why it cannot. Forward slashes on Windows, where a line is not quoted for backslashes.</summary>
    public string ShimPath
    {
        get
        {
            var path = Path.Combine(ProgramDirectory, OperatingSystem.IsWindows() ? "omp.cmd" : "omp");
            return OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        }
    }

    /// <summary>The folder with the program alone, put first on the PATH of the managed omp, so an <c>omp</c> it runs is this one.</summary>
    private string ProgramDirectory => Path.Combine(ShimDirectory, "bin");

    internal string SettingsPath => Path.Combine(ShimDirectory, "settings.yml");

    internal string ManagerPath => Path.Combine(ShimDirectory, "manager.json");

    /// <summary><paramref name="basis"/> with what the pinned copy says: its version, a download that runs and a failure.</summary>
    public OmpStatus Status(OmpStatus basis)
    {
        var state = State();
        return basis with { PinnedVersion = Version, ManagedVersion = state.ManagedVersion, Installing = state.Installing, Progress = state.Progress, Error = state.Error };
    }

    protected override IEnumerable<string> Downloads => [Root, ShimDirectory];

    protected override void Installed(RuntimeResolution bun) => WriteShim(bun);

    /// <summary>
    /// Writes the <c>omp</c> program that runs the managed copy on Bun (<c>bun --bun cli.js "$@"</c>) with Bun's environment, and a <c>omp.cmd</c> on Windows.
    /// The program adds <see cref="SettingsPath"/> after any <c>PI_CONFIG_FILES</c> it inherits (omp lets a later file win) and puts its own folder first
    /// on PATH. Both changes stay inside that run: neither the terminal nor the user's omp files change.
    /// It prints one sentence and exits 1 while the verified copy is missing, or when <paramref name="bun"/> is null (Bun is not there yet). The files are written
    /// atomically and only when their text changed, so a new pinned version or Bun path rewrites the program. Returns <see cref="ShimPath"/>.
    /// </summary>
    public string WriteShim(RuntimeResolution? bun)
    {
        var marker = MarkerPath;
        const string Missing = "omp is not downloaded yet. Open Settings in tereditor, choose omp and press Download.";
        const string NoBun = "omp needs Bun. Turn on Runtimes for tooling in Settings, then press Download on the omp page.";
        var environment = bun?.Env.Where(pair => VariableName().IsMatch(pair.Key)).ToArray() ?? [];
        // The two files come first: the program refuses to start omp without them.
        OwnedFile.Write(SettingsPath, Settings);
        OwnedFile.Write(ManagerPath, Manager);
        string[] needed = [EntryPoint, marker, SettingsPath, ManagerPath];
        string text;
        if (OperatingSystem.IsWindows())
        {
            static string Cmd(string value) => value.Replace("%", "%%");
            var lines = new List<string> { "@echo off", "setlocal" };
            if (bun is { Available: true })
            {
                lines.AddRange(needed.Select(file => $"if not exist \"{file}\" goto missing"));
                lines.AddRange(environment.Select(pair => $"set \"{pair.Key}={Cmd(pair.Value)}\""));
                lines.Add($"set \"PI_CONFIG_FILES=%PI_CONFIG_FILES%;{Cmd(SettingsPath)}\"");
                lines.Add($"set \"PATH={Cmd(ProgramDirectory)};%PATH%\"");
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
                  $"if {string.Join(" || ", needed.Select(file => $"[ ! -f {Quote(file)} ]"))}; then echo {Quote(Missing)} >&2; exit 1; fi\n" +
                  string.Concat(environment.Select(pair => $"export {pair.Key}={Quote(pair.Value)}\n")) +
                  $"export PI_CONFIG_FILES=\"${{PI_CONFIG_FILES:+$PI_CONFIG_FILES:}}\"{Quote(SettingsPath)}\n" +
                  $"export PATH={Quote(ProgramDirectory)}:\"$PATH\"\n" +
                  $"exec {Quote(bun.Command)} --bun {Quote(EntryPoint)} \"$@\"\n"
                : $"#!/bin/sh\n# Written by tereditor: runs the pinned omp on Bun.\necho {Quote(NoBun)} >&2\nexit 1\n";
        }
        OwnedFile.Write(Path.Combine(ProgramDirectory, OperatingSystem.IsWindows() ? "omp.cmd" : "omp"), text,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return ShimPath;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();
}