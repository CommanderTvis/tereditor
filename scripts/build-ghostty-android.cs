#!/usr/bin/env dotnet
// Builds the Skia renderer's native library for Android into an output directory (MSBuild's GhosttyNativeCache):
//   android/<abi>/libGhosttyAvaloniaVt.so  libghostty-vt terminal state behind the Skia renderer's C ABI
// It shares build-ghostty-macos.cs's pinned libghostty-vt source, Zig toolchain (mise.toml) and machine-wide cache, where each
// finished build is kept under results/<key> (see scripts/lib/Native.cs). The NDK is ANDROID_NDK_HOME,
// or the one `scripts/tool android-ndk` prints.
// Usage: scripts/mise x -- dotnet run --file scripts/build-ghostty-android.cs -- <arm64-v8a|x86_64> [output-directory]
#:include lib/Tasks.cs
#:include lib/Native.cs
using static Native;
using static Tasks;

return Guard(() =>
{
    var project = At("src", "Ghostty.Avalonia");
    var abi = args.Length > 0 ? args[0] : "";
    var triple = abi switch
    {
        "arm64-v8a" => "aarch64-linux-android",
        "x86_64" => "x86_64-linux-android",
        _ => throw new TaskFailure("Usage: build-ghostty-android.cs <arm64-v8a|x86_64> [output-directory]"),
    };
    const int api = 24;
    var cache = args.Length > 1 && args[1].Length > 0 ? args[1] : Path.Combine(project, "obj", "native");
    Directory.CreateDirectory(cache);
    cache = Path.GetFullPath(cache);
    var vtCommit = Pin("GHOSTTY_VT_COMMIT");
    var vtZig = Pin("GHOSTTY_ZIG");
    var ndkHost = OperatingSystem.IsMacOS() ? "darwin-x86_64" : OperatingSystem.IsLinux() ? "linux-x86_64" : throw new TaskFailure("The Android build needs macOS or Linux.");
    var ndk = Env("ANDROID_NDK_HOME") ?? Tool("android-ndk");
    if (!File.Exists(Path.Combine(ndk, "source.properties"))) throw new TaskFailure($"No Android NDK at {ndk}; run scripts/mise install with MISE_ENV=android.");
    var work = Path.Combine(CacheDir(), "ghostty-avalonia");
    Directory.CreateDirectory(Path.Combine(work, "results"));
    work = Path.GetFullPath(work);

    var output = Path.Combine(cache, "android", abi);
    var fingerprint = $"{abi} {api} {vtCommit} {vtZig} {ShaSum(Path.Combine(ndk, "source.properties"), Path.Combine(project, "Native", "GhosttyVt.c"), Recipe)}";
    var library = Path.Combine(output, "libGhosttyAvaloniaVt.so");
    if (File.Exists(library) && ReadStamp(Path.Combine(output, "vt.fingerprint")) == fingerprint) return 0;
    // MSBuild may invoke this for several ABIs, alongside the desktop build, and other checkouts share the cache.
    using var buildLock = AcquireLock(Path.Combine(work, "build.lock"));
    var result = Path.Combine(work, "results", $"android-{abi}-vt-{CacheKey(fingerprint)}");

    if (!File.Exists(Path.Combine(result, "libGhosttyAvaloniaVt.so")))
    {
        var zig = ZigExe(vtZig);
        var source = GhosttySource(work, "vt-source", vtCommit);
        Delete(result + ".partial");
        Directory.CreateDirectory(result + ".partial");
        RestoreZigDeps();
        var prefix = Path.Combine(work, $"android-{abi}-vt-out");
        // Ghostty's build takes bionic's headers and startup objects from the NDK and aligns segments to 16 KB pages.
        Check(zig, ["build", "--cache-dir", Path.Combine(work, "vt-cache"), "--prefix", prefix, "-Demit-lib-vt", "-Doptimize=ReleaseFast", $"-Dtarget={triple}.{api}"], source,
            new Dictionary<string, string> { ["ANDROID_NDK_HOME"] = ndk });
        var bin = Path.Combine(ndk, "toolchains", "llvm", "prebuilt", ndkHost, "bin");
        var staged = Path.Combine(result + ".partial", "libGhosttyAvaloniaVt.so");
        Check(Path.Combine(bin, $"{triple}{api}-clang"), ["-shared", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror", "-DGHOSTTY_STATIC",
            "-I", Path.Combine(prefix, "include"), Path.Combine(project, "Native", "GhosttyVt.c"), Path.Combine(prefix, "lib", "libghostty-vt.a"),
            "-Wl,-z,max-page-size=16384", "-Wl,--no-undefined", "-Wl,--exclude-libs,ALL", "-Wl,-soname,libGhosttyAvaloniaVt.so", "-o", staged]);
        Check(Path.Combine(bin, "llvm-strip"), ["--strip-unneeded", staged]);
        PublishResult(result);
    }
    InstallOutput(result, output, "libGhosttyAvaloniaVt.so");
    File.WriteAllText(Path.Combine(output, "vt.fingerprint"), fingerprint + "\n");
    return 0;
});
