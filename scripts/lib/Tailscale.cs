// The Go toolchain shared by the Tailscale and Headscale recipes, through `#:include` with lib/Tasks.cs and lib/Native.cs.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using static Native;
using static Tasks;

static class TailscaleGo
{
    const string Version = "1.27.2";

    /// <summary>
    /// Installs the checksum-pinned Go toolchain shared by the Tailscale builds into the machine-wide cache
    /// ($TEREDITOR_CACHE_DIR/tailscale-go/go&lt;version&gt;-&lt;platform&gt;, one directory per pin so checkouts pinning different versions never
    /// replace each other's) and returns its GOROOT. Go's build and module caches beside it are safe for concurrent use.
    /// </summary>
    public static string Provision()
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var intel = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        var (platform, checksum) = (OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), arm, intel) switch
        {
            (true, _, true, _) => ("darwin-arm64", "76812b213b1b2302c978d28fa52fa92d541704b9e7d9d5db8002c50e4018c4c5"),
            (true, _, _, true) => ("darwin-amd64", "587b59182488b23aa6e5fc25110405a3e0e5b38ed2f5b2f46ed13c32aee356fe"),
            (_, true, true, _) => ("linux-arm64", "94f3e30b8e374bc285e7dadc11e0865726b9bc6e85b841ccceaabc0214c6b7c8"),
            (_, true, _, true) => ("linux-amd64", "ecbadb99091a3f46e31f5f934b068b1864eafa7995211b39eaddf76996045fe5"),
            _ => throw new TaskFailure("Tailscale builds require macOS or Linux, arm64 or x64."),
        };
        var cache = Path.Combine(CacheDir(), "tailscale-go");
        var goroot = Path.Combine(cache, $"go{Version}-{platform}");
        bool Ready() => File.Exists(Path.Combine(goroot, "bin", "go"));
        if (!Ready())
        {
            Directory.CreateDirectory(cache);
            using var _ = AcquireLock(Path.Combine(cache, "install.lock"));
            if (!Ready())
            {
                var partial = goroot + ".partial";
                Delete(partial);
                Directory.CreateDirectory(partial);
                var archive = Path.Combine(partial, "go.tar.gz");
                Download($"https://go.dev/dl/go{Version}.{platform}.tar.gz", archive, 3);
                VerifySha256(archive, checksum);
                Check("tar", ["-xzf", archive, "-C", partial]);
                Directory.Move(Path.Combine(partial, "go"), goroot);
                Delete(partial);
            }
        }
        return goroot;
    }

    public static string Go(string goroot) => Path.Combine(goroot, "bin", "go");

    /// <summary>Points Go at the machine-wide build and module caches beside the toolchain, and at no other toolchain.</summary>
    public static void UseCaches(string goroot)
    {
        var cache = Path.GetDirectoryName(goroot)!;
        Environment.SetEnvironmentVariable("GOTOOLCHAIN", "local");
        Environment.SetEnvironmentVariable("GOCACHE", Path.Combine(cache, "build-cache"));
        Environment.SetEnvironmentVariable("GOMODCACHE", Path.Combine(cache, "modules"));
    }

    /// <summary>The tailscale.com version a go.mod requires, or "" when it names none.</summary>
    public static string ModuleVersion(string goMod) =>
        Regex.Match(File.ReadAllText(goMod), @"^require tailscale\.com v([0-9][0-9.]*)\r?$", RegexOptions.Multiline).Groups[1].Value;

    /// <summary>The modules of the build in a directory that are in the module cache, with their directories (go list -m all).</summary>
    public static List<(string Module, string Directory)> Modules(string goroot, string directory)
    {
        var (code, output) = Capture(Go(goroot), ["list", "-m", "-f", "{{if .Dir}}{{.Path}}|{{.Dir}}{{end}}", "all"], directory);
        if (code != 0) throw new TaskFailure($"go list failed with exit code {code}", code);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('|', 2))
            .Where(fields => fields.Length == 2 && fields[1].Length > 0).Select(fields => (fields[0], fields[1])).ToList();
    }

    /// <summary>The licence, copying and notice files in a module's top directory (the shell globs LICENSE*, COPYING*, NOTICE*), in that order.</summary>
    public static IEnumerable<string> LicenseFiles(string directory)
    {
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseSensitive };
        foreach (var pattern in new[] { "LICENSE*", "COPYING*", "NOTICE*" })
            foreach (var file in Directory.EnumerateFiles(directory, pattern, options).Order(StringComparer.Ordinal)) yield return file;
    }

    /// <summary>Module cache licences are read-only; keep the copies writable so repeated builds replace them cleanly.</summary>
    public static void CopyWritable(string source, string target)
    {
        if (File.Exists(target)) MakeWritable(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, true);
        MakeWritable(target);
    }

    static void MakeWritable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserWrite);
    }

    /// <summary>One entry of a notices bundle: a header naming the licence, the licence and a newline.</summary>
    public static void AppendNotice(Stream bundle, string name, string licensePath)
    {
        bundle.Write(Encoding.UTF8.GetBytes($"\n===== {name} =====\n\n"));
        using (var input = File.OpenRead(licensePath)) input.CopyTo(bundle);
        bundle.Write("\n"u8);
    }

    public static string TereditorVersionStamp(string tsversion) => tsversion + "-tereditor";
}
