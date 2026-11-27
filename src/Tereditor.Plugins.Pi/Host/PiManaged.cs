using Tereditor.Plugins.Agent.Host;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>The pinned Pi that tereditor downloads (<see cref="ManagedBunPackage"/>), and how a chat runs it.</summary>
internal sealed class PiManaged(string root, Func<string, CancellationToken, Task<RuntimeResolution>> resolveBun, Func<string, CancellationToken, Task<RuntimeResolution>> findBun)
    : ManagedBunPackage(new("Pi", Package, Version, Integrity, "dist/bundle/cli.js"), root, resolveBun)
{
    public const string Package = "@earendil-works/pi-coding-agent";
    public const string Version = "1.6.0";
    /// <summary>The npm registry's <c>dist.integrity</c> of <see cref="Package"/> at <see cref="Version"/>; change both together.</summary>
    public const string Integrity = "sha512-zX1BVEsshx7A2MRuQ6jfr3RJGWw4Wn+vLq5ZfmlvS1RskhRmByaSZVXH/zcwRrWHO7qPgs98pNCbPOEti/t0fQ==";
    public const string NotDownloaded = "Pi is not downloaded yet. Open Settings, choose Pi and press Download.";
    public const string NoBun = "Pi needs Bun. Open Settings, choose Runtimes and press Download, or install Bun yourself.";
    /// <summary>
    /// What <c>pi update</c> runs in place of an installer: Pi splits <c>PI_SELF_UPDATE_COMMAND</c> on spaces and starts the words without a shell,
    /// so this only prints a sentence. Only tereditor's Update button changes the pinned version.
    /// </summary>
    public const string SelfUpdateCommand = "echo tereditor keeps this Pi at its pinned version. Update it from the Pi page in tereditor.";

    public static string DefaultRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "pi", "managed");

    /// <summary>The status of the pinned copy; with <paramref name="source"/> managed it is the whole answer, otherwise it is added to the system Pi's status.</summary>
    public PiStatus Status(string source, PiStatus? system = null)
    {
        var state = State();
        var installed = state.InstalledVersion;
        var basis = source == PiSources.Managed
            ? new PiStatus(installed.Length > 0, "managed") { Version = installed.Length > 0 ? installed : null, Detail = installed.Length > 0 ? null : "The pinned Pi is not downloaded yet." }
            : system!;
        return basis with { Source = source, PinnedVersion = Version, ManagedVersion = state.ManagedVersion, Installing = state.Installing, Progress = state.Progress, Error = state.Error };
    }

    /// <summary>
    /// Returns <paramref name="spawn"/> as the command that runs the pinned Pi on Bun (<c>bun --bun cli.js --mode rpc …</c>). It never downloads Pi or Bun:
    /// a missing copy or a missing Bun fails with a sentence that points to Settings. A blocked spawn is returned as it is, so it fails with its reason.
    /// The run gets Pi's own switches against updates (<c>docs/environment-variables.md</c> in the package): no version check, and <see cref="SelfUpdateCommand"/>.
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
        environment["PI_SKIP_VERSION_CHECK"] = "1";
        environment["PI_SELF_UPDATE_COMMAND"] = SelfUpdateCommand;
        return spawn with { Command = bun.Command, Prefix = ["--bun", EntryPoint], Environment = environment, Started = id => track(bun, id) };
    }
}