using System.Text.RegularExpressions;

using Tereditor.Plugins.Agent.Host;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Omp.Host;

/// <summary>
/// The pinned omp that tereditor downloads (<see cref="ManagedBunPackage"/>). The launch lines of omp start a small program
/// named <c>omp</c> that tereditor writes (<see cref="ShimPath"/>) and that runs the copy, so a line keeps its executable word and the other
/// plugins' launch contributions still apply to it.
/// </summary>
/// <remarks>
/// Package facts: https://registry.npmjs.org/@oh-my-pi/pi-coding-agent/19.1.1 (the <c>omp</c> binary is <c>dist/cli.js</c>, engines <c>bun &gt;=1.4.4</c>;
/// Runtimes for tooling pins Bun 1.4.4).
/// </remarks>
internal sealed partial class OmpManaged(string root, Func<string, CancellationToken, Task<RuntimeResolution>> resolveBun)
    : ManagedBunPackage(new("omp", Package, Version, Integrity, "dist/cli.js"), root, resolveBun)
{
    public const string Package = "@oh-my-pi/pi-coding-agent";
    public const string Version = "19.1.1";
    /// <summary>The npm registry's <c>dist.integrity</c> of <see cref="Package"/> at <see cref="Version"/>; change both together.</summary>
    public const string Integrity = "sha512-x/7xQXQ5F/ga4LTNOCSQs+cggWY1H5EF34+juTPP9QOyoFgfmOFVspXA4cBCp9EH8D967oje/HiCPpoNsWAyhw==";

    /// <summary><c>~/.tereditor/omp/managed</c>; checks move the home with <see cref="OmpHost.HomeVariable"/>, so they never touch the real one.</summary>
    public static string DefaultRoot() => Path.Combine(Environment.GetEnvironmentVariable(OmpHost.HomeVariable) is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "omp", "managed");

    /// <summary>The folder of the <c>omp</c> program next to the managed folder (<c>~/.tereditor/omp/shim</c>).</summary>
    public string ShimDirectory => Path.Combine(Path.GetDirectoryName(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? Root, "shim");

    /// <summary>The program a launch line starts: it runs the managed omp on Bun, or says why it cannot. Forward slashes on Windows, where a line is not quoted for backslashes.</summary>
    public string ShimPath
    {
        get
        {
            var path = Path.Combine(ShimDirectory, OperatingSystem.IsWindows() ? "omp.cmd" : "omp");
            return OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        }
    }

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
    /// It prints one sentence and exits 1 while the verified copy is missing, or when <paramref name="bun"/> is null (Bun is not there yet). The file is written
    /// atomically and only when its text changed, so a new pinned version or Bun path rewrites it. Returns <see cref="ShimPath"/>.
    /// </summary>
    public string WriteShim(RuntimeResolution? bun)
    {
        var marker = MarkerPath;
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
        OwnedFile.Write(Path.Combine(ShimDirectory, OperatingSystem.IsWindows() ? "omp.cmd" : "omp"), text,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return ShimPath;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();
}