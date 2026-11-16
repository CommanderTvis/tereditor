#!/usr/bin/env dotnet
// Builds the Skia renderer's native library for Windows into an output directory (MSBuild's GhosttyNativeCache):
//   <win-x64|win-arm64>/GhosttyAvaloniaVt.dll  libghostty-vt terminal state behind the Skia renderer's C ABI, and its `fingerprint` stamp
// The output directory is the checkout's. The pinned source, the Zig cache and each finished build (results/<key>, the
// key hashing everything the build reads) live in the machine-wide cache every checkout shares, like build-ghostty-macos.cs's.
// Zig cross-compiles, so the Zig that mise installs for the host (http:zig-windows in mise.toml) builds either target.
// Usage: scripts\mise.cmd x -- dotnet run --file scripts/build-ghostty-windows.cs -- [win-x64|win-arm64] [output-directory]
#:include lib/Tasks.cs
#:include lib/Native.cs
using System.Runtime.InteropServices;
using static Native;
using static Tasks;

// The CPU of every Windows native build. Zig builds for the detected CPU when the target is the host's own architecture and OS,
// which tied the shipped libraries to the build machine's features and, on the Windows ARM64 runner, made Zig 0.16 pass
// a mangled feature list to Clang (unknown argument '-fuse-crypto-eor'). Baseline is the same everywhere.
const string cpu = "baseline";

var zigDiagnosed = false;

return Guard(() =>
{
    var project = At("src", "Ghostty.Avalonia");
    var rid = args.Length > 0 && args[0].Length > 0 ? args[0]
        : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
    if (rid is not ("win-x64" or "win-arm64")) throw new TaskFailure($"Windows natives support win-x64 and win-arm64 only, not {rid}.");
    var target = rid == "win-arm64" ? "aarch64-windows-gnu" : "x86_64-windows-gnu";
    var cache = Path.GetFullPath(args.Length > 1 && args[1].Length > 0 ? args[1] : Path.Combine(project, "obj", "native"));
    var output = Path.Combine(cache, rid);
    Directory.CreateDirectory(output);
    var commit = Pin("GHOSTTY_VT_COMMIT");
    var native = Path.Combine(project, "Native");
    var fingerprint = commit + target + cpu + string.Concat(new[] { Recipe, Path.Combine(native, "GhosttyVt.c"), Path.Combine(native, "GhosttyClipboardStub.c") }
        .Order(StringComparer.Ordinal).Select(file => HashFiles(file).ToUpperInvariant()));
    var stamp = Path.Combine(output, "fingerprint");
    var library = Path.Combine(output, "GhosttyAvaloniaVt.dll");
    if (File.Exists(library) && ReadStamp(stamp)?.Trim() == fingerprint) return 0;
    var work = Path.Combine(CacheDir(), "ghostty-avalonia");
    Directory.CreateDirectory(work);
    using var buildLock = AcquireLock(Path.Combine(work, "build.lock"));
    var result = Path.Combine(work, "results", $"{rid}-vt-{CacheKey(fingerprint)}");
    if (!File.Exists(Path.Combine(result, "GhosttyAvaloniaVt.dll")))
    {
        var zig = Path.GetFullPath(Path.Combine(Tool("zig-windows"), "zig.exe"));
        if (!File.Exists(zig)) throw new TaskFailure($"No Zig at {zig}.");
        var source = Path.Combine(work, "windows-vt-source");
        if (GitHead(source) != commit)
        {
            Delete(source);
            Delete(source + ".partial");
            FetchGit(source + ".partial", GhosttyUrl, commit);
            Directory.Move(source + ".partial", source);
        }
        var build = Path.Combine(work, rid);
        var prefix = Path.Combine(build, "vt");
        var staging = result + ".partial";
        Delete(staging);
        Directory.CreateDirectory(staging);
        RestoreZigDeps();
        InvokeZig(zig, rid, build, ["build", "--cache-dir", Path.Combine(build, "zig-cache"), "--prefix", prefix, "-Demit-lib-vt", "-Doptimize=ReleaseFast", $"-Dtarget={target}", $"-Dcpu={cpu}"], source);
        InvokeZig(zig, rid, build, ["cc", "-target", target, $"-mcpu={cpu}", "-shared", "-O2", "-Wall", "-Wextra", "-Werror", "-DGHOSTTY_STATIC",
            "-I", Path.Combine(prefix, "include"), Path.Combine(native, "GhosttyVt.c"), Path.Combine(native, "GhosttyClipboardStub.c"),
            Path.Combine(prefix, "lib", "ghostty-vt-static.lib"), "-o", Path.Combine(staging, "GhosttyAvaloniaVt.dll")], null);
        PublishResult(result);
    }
    File.Copy(Path.Combine(result, "GhosttyAvaloniaVt.dll"), library + ".partial", true);
    File.Move(library + ".partial", library, true);
    File.WriteAllText(stamp, fingerprint + Environment.NewLine);
    return 0;
});

// Runs Zig for one native build and keeps what a crash needs on the run page: the host, Zig's version and env, each
// command line, its output, the exit code in hex and, on failure, the Windows crash records and the tail of the build's
// Zig cache. It changes no behaviour: no retry, lock or cache redirection.
void InvokeZig(string zig, string rid, string build, string[] arguments, string? directory)
{
    if (!zigDiagnosed)
    {
        zigDiagnosed = true;
        Console.WriteLine($"Zig host: OS {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}, target {rid}");
        foreach (var diagnostic in new[] { "version", "env" })
        {
            Console.WriteLine($"> {zig} {diagnostic}");
            Run(zig, [diagnostic]);
        }
    }
    Console.WriteLine($"> {zig} {string.Join(' ', arguments)}");
    var code = Run(zig, arguments, directory);
    if (code == 0) return;
    var message = $"{zig} {string.Join(' ', arguments)} failed with exit code {code} (0x{code:X8})";
    Console.WriteLine($"::error title=Zig failed::{message.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A")}");
    Console.WriteLine("--- Application Error events (newest 5) ---");
    Run("powershell", ["-NoProfile", "-Command",
        "try { Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error' } -MaxEvents 5 -ErrorAction Stop | Format-List TimeCreated, Message | Out-String | Write-Host } catch { Write-Host \"No Application Error events: $($_.Exception.Message)\" }"]);
    var zigCache = Path.Combine(build, "zig-cache");
    if (Directory.Exists(zigCache))
    {
        Console.WriteLine($"--- Last 50 lines of the newest files in {zigCache} ---");
        var newest = new DirectoryInfo(zigCache).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => (file.Extension is ".txt" or ".log" or ".d" or "") && file.Length < 1024 * 1024)
            .OrderByDescending(file => file.LastWriteTimeUtc).Take(3);
        foreach (var file in newest)
        {
            Console.WriteLine($"# {file.FullName}");
            try { foreach (var line in File.ReadLines(file.FullName).TakeLast(50)) Console.WriteLine(line); } catch (IOException) { }
        }
    }
    throw new TaskFailure(message, code);
}
