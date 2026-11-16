#!/usr/bin/env dotnet
// Builds the Skia renderer's native library for Linux into an output directory (MSBuild's GhosttyNativeCache):
//   linux-<arch>/libGhosttyAvaloniaVt.so  libghostty-vt terminal state behind the Skia renderer's C ABI
// It shares build-ghostty-macos.cs's pinned libghostty-vt source, Zig toolchain (mise.toml) and machine-wide cache, where each
// finished build is kept under results/<key> (see scripts/lib/Native.cs) and cross-compiles with Zig from any
// macOS or Linux host, against glibc 2.31 (the baseline AppImages are built for) so the library runs on every distribution
// at least that old. Zig links only libc, libm and libdl; nothing else is needed at run time.
// Usage: scripts/mise x -- dotnet run --file scripts/build-ghostty-linux.cs -- <linux-x64|linux-arm64> [output-directory]
#:include lib/Tasks.cs
#:include lib/Native.cs
using static Native;
using static Tasks;

return Guard(() =>
{
    var project = At("src", "Ghostty.Avalonia");
    var rid = args.Length > 0 ? args[0] : "";
    var target = rid switch
    {
        "linux-x64" => "x86_64-linux-gnu.2.31",
        "linux-arm64" => "aarch64-linux-gnu.2.31",
        _ => throw new TaskFailure("Usage: build-ghostty-linux.cs <linux-x64|linux-arm64> [output-directory]"),
    };
    var cache = args.Length > 1 && args[1].Length > 0 ? args[1] : Path.Combine(project, "obj", "native");
    Directory.CreateDirectory(cache);
    cache = Path.GetFullPath(cache);
    var vtCommit = Pin("GHOSTTY_VT_COMMIT");
    var vtZig = Pin("GHOSTTY_ZIG");
    var work = Path.Combine(CacheDir(), "ghostty-avalonia");
    Directory.CreateDirectory(Path.Combine(work, "results"));
    work = Path.GetFullPath(work);

    var output = Path.Combine(cache, rid);
    var zig = ZigExe(vtZig);
    var (code, version) = Capture(zig, ["version"]);
    if (code != 0) throw new TaskFailure($"{zig} version failed with exit code {code}", code);
    var fingerprint = $"{vtCommit} {target} {version.Trim()} {ShaSum(Path.Combine(project, "Native", "GhosttyVt.c"), Recipe)}";
    var library = Path.Combine(output, "libGhosttyAvaloniaVt.so");
    if (File.Exists(library) && ReadStamp(Path.Combine(output, "vt.fingerprint")) == fingerprint) return 0;
    // MSBuild may invoke this alongside other native builds, and other checkouts share the cache.
    using var buildLock = AcquireLock(Path.Combine(work, "build.lock"));
    var result = Path.Combine(work, "results", $"{rid}-vt-{CacheKey(fingerprint)}");

    if (!File.Exists(Path.Combine(result, "libGhosttyAvaloniaVt.so")))
    {
        var source = GhosttySource(work, "vt-source", vtCommit);
        Delete(result + ".partial");
        Directory.CreateDirectory(result + ".partial");
        RestoreZigDeps();
        var prefix = Path.Combine(work, rid + "-vt-out");
        Check(zig, ["build", "--cache-dir", Path.Combine(work, "vt-cache"), "--prefix", prefix, "-Demit-lib-vt", "-Doptimize=ReleaseFast", "-Dtarget=" + target], source);
        // The version script exports the bridge alone, so Ghostty's own symbols stay private to the library.
        var exports = Path.Combine(work, rid + "-exports.map");
        File.WriteAllText(exports, "{ global: gav_*; local: *; };\n");
        Check(zig, ["cc", "-target", target, "-shared", "-fPIC", "-s", "-O2", "-Wall", "-Wextra", "-Werror", "-DGHOSTTY_STATIC",
            "-I", Path.Combine(prefix, "include"), Path.Combine(project, "Native", "GhosttyVt.c"), Path.Combine(prefix, "lib", "libghostty-vt.a"),
            "-Wl,--no-undefined", "-Wl,--gc-sections", "-Wl,--version-script," + exports, "-Wl,-soname,libGhosttyAvaloniaVt.so",
            "-o", Path.Combine(result + ".partial", "libGhosttyAvaloniaVt.so")]);
        PublishResult(result);
    }
    InstallOutput(result, output, "libGhosttyAvaloniaVt.so");
    File.WriteAllText(Path.Combine(output, "vt.fingerprint"), fingerprint + "\n");
    return 0;
});
