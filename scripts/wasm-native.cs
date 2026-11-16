#!/usr/bin/env dotnet
// Builds the WASM static library libghostty-vt into the browser project's obj/wasm-native. The browser project's
// BuildBrowserNative target decides when it runs: when a source, this script or the lockfile is newer than the library.
// The patched source, Zig cache and finished libraries (results/<key>, the key hashing everything the build reads) live in
// the machine-wide cache every checkout shares; scripts/lib/Native.cs describes the locking and atomic publishing.
// Usage: scripts/mise x -- dotnet run --file scripts/wasm-native.cs   (with MISE_ENV=browser)
#:include lib/Tasks.cs
#:include lib/Native.cs
using System.Runtime.InteropServices;
using static Native;
using static Tasks;

return Guard(() =>
{
    var output = At("src", "Tereditor.Browser", "obj", "wasm-native");
    var cache = Path.Combine(CacheDir(), "wasm-native");
    Directory.CreateDirectory(Path.Combine(cache, "results"));
    Directory.CreateDirectory(output);
    var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    var runtime = (OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), arm, RuntimeInformation.ProcessArchitecture == Architecture.X64) switch
    {
        (true, _, true, _) => "osx-arm64",
        (true, _, _, true) => "osx-x64",
        (_, true, _, true) => "linux-x64",
        (_, true, true, _) => "linux-arm64",
        _ => throw new TaskFailure("Unsupported WASM build host."),
    };
    var packs = Path.Combine(Tool("dotnet"), "packs");
    // The first pack directory of the given kind, with its version directory's path below it.
    IEnumerable<string> Versions(string kind) => !Directory.Exists(packs) ? Enumerable.Empty<string>() : Directory.EnumerateDirectories(packs, $"Microsoft.NET.Runtime.Emscripten.*.{kind}.{runtime}")
        .Order(StringComparer.Ordinal).SelectMany(pack => Directory.EnumerateDirectories(pack).Order(StringComparer.Ordinal));
    string? Find(string kind, string relative, bool directory) => Versions(kind).Select(version => Path.Combine(version, relative))
        .FirstOrDefault(path => directory ? Directory.Exists(path) : File.Exists(path));
    var sdk = Find("Sdk", "tools", true);
    var python = Find("Python", Path.Combine("tools", "bin", "python3"), false);
    var node = Find("Node", Path.Combine("tools", "bin", "node"), false) ?? "";
    var emcache = Find("Cache", Path.Combine("tools", "emscripten", "cache"), true) ?? "";
    // The Python pack exists only for Windows and macOS; Linux hosts use the system python3.
    python ??= (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory, "python3")).FirstOrDefault(File.Exists);
    if (sdk is null || python is null) throw new TaskFailure("Install the checkout-local wasm-tools workload first.");
    string sdkRoot = sdk, pythonPath = python;
    Environment.SetEnvironmentVariable("DOTNET_EMSCRIPTEN_LLVM_ROOT", Path.Combine(sdkRoot, "bin"));
    Environment.SetEnvironmentVariable("DOTNET_EMSCRIPTEN_BINARYEN_ROOT", sdkRoot);
    Environment.SetEnvironmentVariable("DOTNET_EMSCRIPTEN_NODE_JS", node);
    Environment.SetEnvironmentVariable("EM_CACHE", emcache);
    Environment.SetEnvironmentVariable("FROZEN_CACHE", "False");
    void Emscripten(string tool, params string[] arguments) => Check(pythonPath, [Path.Combine(sdkRoot, "emscripten", tool + ".py"), .. arguments]);

    var commit = Pin("GHOSTTY_VT_COMMIT");
    var zigVersion = Pin("GHOSTTY_ZIG");
    var zig = ZigExe(zigVersion);
    var native = At("src", "Ghostty.Avalonia", "Native");
    var patch = Path.Combine(native, "Wasm.patch");
    var patchHash = HashFiles(patch)[..16];
    var fingerprint = $"{commit} {patchHash} {zigVersion} {sdkRoot} {ShaSum(Path.Combine(native, "GhosttyVt.c"), Recipe)}";
    using var buildLock = AcquireLock(Path.Combine(cache, "build.lock"));
    var result = Path.Combine(cache, "results", CacheKey(fingerprint));
    if (!File.Exists(Path.Combine(result, "GhosttyAvaloniaVt.a")))
    {
        var source = Path.Combine(cache, $"ghostty-{commit}-{patchHash}");
        if (!Directory.Exists(source))
        {
            Delete(source + ".partial");
            FetchGit(source + ".partial", GhosttyUrl, commit);
            ApplyPatch(source + ".partial", patch);
            Directory.Move(source + ".partial", source);
        }
        if (GitHead(source) != commit) throw new TaskFailure("Unexpected Ghostty source revision.");
        Delete(result + ".partial");
        Directory.CreateDirectory(result + ".partial");
        RestoreZigDeps();
        Check(zig, ["build", "--cache-dir", Path.Combine(cache, "zig-cache"), "--prefix", Path.Combine(cache, "ghostty"), "-Demit-lib-vt",
            "-Dtarget=wasm32-freestanding", "-Dcpu=mvp+simd128+mutable_globals+sign_ext+nontrapping_fptoint+atomics+bulk_memory", "-Doptimize=ReleaseFast", "-Dstrip=true"], source);
        var staged = Path.Combine(result + ".partial", "GhosttyVt.o");
        Emscripten("emcc", "-pthread", "-O2", "-DGHOSTTY_STATIC", "-I", Path.Combine(source, "include"), "-c", Path.Combine(native, "GhosttyVt.c"), "-o", staged);
        var library = Path.Combine(result + ".partial", "GhosttyAvaloniaVt.a");
        File.Copy(Path.Combine(cache, "ghostty", "lib", "libghostty-vt.a"), library);
        Emscripten("emar", "r", library, staged);
        File.Delete(staged);
        PublishResult(result);
    }
    InstallOutput(result, output, "GhosttyAvaloniaVt.a");
    return 0;
});
