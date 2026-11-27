using Tereditor.Plugins.Agent.Host;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Checks;

/// <summary>
/// What the pinned Pi and the pinned omp share (<see cref="ManagedBunPackage"/>), checked against a fixture bun for both: nothing here
/// touches the network.
/// </summary>
internal static class ManagedAgentChecks
{
    /// <summary>
    /// A fixture bun in <paramref name="directory"/>: `install` writes the package with a lock file that names the pinned integrity, or another
    /// one (mode bad), or waits (modes hang and slow); `--bun &lt;script&gt; …` hands over to the fixture <paramref name="program"/> next to it.
    /// It records its arguments, working directory and BUN_INSTALL.
    /// </summary>
    internal static string FakeBun(string directory, string package, string version, string integrity, string script, string program)
    {
        var path = Path.Combine(directory, "bun");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            here="$(dirname "$0")"
            printf '%s\n' "$*" >> "$here/bun-calls.txt"
            if [ "$1" = "install" ]; then
              printf '%s|%s\n' "$PWD" "$BUN_INSTALL" >> "$here/bun-install.txt"
              mode=$(cat "$here/bun-mode.txt")
              [ "$mode" = "hang" ] && sleep 30
              [ "$mode" = "slow" ] && sleep 1
              pkg=node_modules/{{package}}
              mkdir -p "$(dirname "$pkg/{{script}}")"
              printf '{"name":"x","version":"{{version}}"}' > "$pkg/package.json"
              echo "// cli" > "$pkg/{{script}}"
              if [ "$mode" = "bad" ]; then echo '"{{package}}": ["x", "sha512-other"]' > bun.lock; else echo '"{{package}}": ["x", "{{integrity}}"]' > bun.lock; fi
              exit 0
            fi
            if [ "$1" = "--bun" ]; then shift 2; exec "$here/{{program}}" "$@"; fi
            exit 3
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>The fixture bun as Runtimes would resolve it.</summary>
    internal static RuntimeResolution Resolution(string directory) =>
        new(RuntimeKind.Bun, true, Path.Combine(directory, "bun"), ["x"], new Dictionary<string, string> { ["BUN_INSTALL"] = Path.Combine(directory, "bun-home") }, RuntimeSource.Downloaded, "", false);

    internal static string[] Calls(string directory) => File.Exists(Path.Combine(directory, "bun-calls.txt")) ? File.ReadAllLines(Path.Combine(directory, "bun-calls.txt")) : [];

    internal static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("expected " + typeof(T).Name);
    }

    /// <summary>
    /// A refused, a canceled and a good download of <paramref name="managed"/> through the fixture bun in <paramref name="directory"/>. It ends with
    /// the pinned copy installed and no recorded Bun call. <paramref name="shown"/> is what the plugin's status says about the download.
    /// </summary>
    internal static async Task Download(ManagedBunPackage managed, string directory, string package, string version, string workspace, Func<(bool Installing, string Progress, string Error)> shown)
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(managed.Name + ": " + message);
        }
        void Mode(string value) => File.WriteAllText(Path.Combine(directory, "bun-mode.txt"), value);
        var install = managed.Root;
        bool Empty() => !Directory.Exists(install) || Directory.GetFileSystemEntries(install).Length == 0;

        Mode("bad");
        var refused = await Throws<InvalidOperationException>(() => managed.EnsureAsync(workspace, CancellationToken.None));
        Require(refused.Message.Contains("checksum", StringComparison.Ordinal) && managed.LastError.Contains(managed.Name + " could not be downloaded", StringComparison.Ordinal) && shown() is { Installing: false, Error.Length: > 0 },
            "A download whose lock file does not name the pinned integrity is refused with a reason.");
        Require(Empty() && managed.InstalledVersion().Length == 0, "A refused download leaves nothing behind.");

        Mode("hang");
        var hanging = managed.EnsureAsync(workspace, CancellationToken.None);
        for (var wait = 0; wait < 400 && shown() is not { Installing: true, Progress.Length: > 0 }; wait++) await Task.Delay(25);
        Require(managed.Running && shown() is { Installing: true, Progress.Length: > 0, Error: "" }, "A running download reports that it runs, with a progress line.");
        managed.Cancel();
        await Throws<OperationCanceledException>(() => hanging);
        Require(Empty() && managed.LastError.Contains("canceled", StringComparison.Ordinal) && !managed.Running, "Cancel stops Bun, leaves no partial folder and says so.");

        // An older pin's copy is shown as such and replaced by the update.
        Directory.CreateDirectory(Path.Combine(install, "0.1.0"));
        Require(managed.State() is { ManagedVersion: "0.1.0", InstalledVersion: "" }, "A copy from an earlier pin is not the pinned one, so the page offers an update.");
        File.Delete(Path.Combine(directory, "bun-calls.txt"));
        Mode("slow");
        await Task.WhenAll(managed.EnsureAsync(workspace, CancellationToken.None), managed.EnsureAsync(workspace, CancellationToken.None));
        Require(Calls(directory).Count(line => line.StartsWith("install", StringComparison.Ordinal)) == 1, "Two requests for the pinned copy share one download.");
        Require(Calls(directory)[0] == "install --ignore-scripts --no-progress --registry=https://registry.npmjs.org/", "Bun installs without the package's scripts, from the npm registry.");
        var line = File.ReadAllLines(Path.Combine(directory, "bun-install.txt")).Last().Split('|');
        Require(Path.GetFullPath(line[0]).StartsWith(Path.GetFullPath(install), StringComparison.Ordinal) && line[1] == Path.Combine(directory, "bun-home"),
            "Bun installs in the download folder, never the workspace, with the host's Bun environment and no global packages.");
        Require(managed.InstalledVersion() == version && managed.State().ManagedVersion == version, "A checked download is the installed pinned copy.");
        Require(!Directory.Exists(Path.Combine(install, "0.1.0")) && Directory.GetFileSystemEntries(install).Length == 1, "The update removes the earlier copy and leaves one folder.");
        Require(File.ReadAllText(Path.Combine([install, version, "node_modules", .. package.Split('/'), "package.json"])).Contains(version, StringComparison.Ordinal), "The package is installed at the pinned version.");
        File.Delete(Path.Combine(directory, "bun-calls.txt"));
        await managed.EnsureAsync(workspace, CancellationToken.None);
        Require(Calls(directory).Length == 0, "An installed pinned copy is not downloaded again.");
    }
}