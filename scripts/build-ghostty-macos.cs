#!/usr/bin/env dotnet
// Builds Ghostty.Avalonia's native libraries into an output directory (MSBuild's GhosttyNativeCache, under the checkout's obj):
//   libGhosttyAvaloniaView.dylib  embedded libghostty (1.4 line): AppKit view with Ghostty's Metal renderer
//   libGhosttyAvaloniaVt.dylib    libghostty-vt terminal state behind the Skia renderer's C ABI
//   resources/                    Ghostty's terminfo, used by the embedded view (its GPL-3.0 shell integration is not shipped;
//                                 tereditor injects its own)
// The pinned sources, Zig caches and finished builds live in the machine-wide cache ($TEREDITOR_CACHE_DIR/ghostty-avalonia,
// shared with build-ghostty-linux.cs and build-ghostty-android.cs): each finished build is kept under results/<key>, the key
// hashing everything the build reads, so checkouts on different branches never overwrite each other's libraries and a new
// worktree copies an existing result instead of rebuilding. One lock serializes the builds of every checkout.
// The pins live in mise.toml's [env]; run this through `scripts/mise x --` (the MSBuild targets do). Both libraries build
// with the one Zig of mise.toml.
// Usage: scripts/mise x -- dotnet run --file scripts/build-ghostty-macos.cs -- [osx-<arch>] [output-directory]
#:include lib/Tasks.cs
#:include lib/Native.cs
using System.Runtime.InteropServices;
using static Native;
using static Tasks;

return Guard(() =>
{
    if (!OperatingSystem.IsMacOS()) return 0;
    var project = At("src", "Ghostty.Avalonia");
    var native = Path.Combine(project, "Native");
    var cache = args.Length > 1 && args[1].Length > 0 ? args[1] : Path.Combine(project, "obj", "native");
    Directory.CreateDirectory(cache);
    cache = Path.GetFullPath(cache);
    var viewCommit = Pin("GHOSTTY_VIEW_COMMIT");
    var vtCommit = Pin("GHOSTTY_VT_COMMIT");
    var zigVersion = Pin("GHOSTTY_ZIG");
    var arch = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "arm64", Architecture.X64 => "x86_64", _ => throw new TaskFailure("") };
    var ridArch = arch == "arm64" ? "arm64" : "x64";
    if (args.Length > 0 && args[0].Length > 0 && args[0] != "osx-" + ridArch)
        throw new TaskFailure("The Ghostty libraries must be built on their target macOS architecture.");
    var work = Path.Combine(CacheDir(), "ghostty-avalonia");
    Directory.CreateDirectory(Path.Combine(work, "results"));
    work = Path.GetFullPath(work);

    string[] InNative(params string[] names) => names.Select(name => Path.Combine(native, name)).ToArray();
    bool Fresh(string stamp, string library, string fingerprint) =>
        File.Exists(Path.Combine(cache, stamp + ".fingerprint")) && File.Exists(Path.Combine(cache, library)) && ReadStamp(Path.Combine(cache, stamp + ".fingerprint")) == fingerprint;

    var viewFingerprint = $"{arch} {viewCommit} {zigVersion} " + ShaSum([.. InNative("GhosttyView.h", "GhosttyView.m", "GhosttyTexture.m", "MetalTexture.patch", "LinkUnderlines.patch", "ExternalIo.patch",
        "keyboard-mode.zig", "file-links.zig", "line-links.zig", "context-link.zig", "prompt-surface.zig", "prompts.zig", "reclaim.zig", "reclaim-surface.zig", "Reclaim.patch", "FileLinks.patch", "CommandEnd.patch"), Recipe]);
    var vtFingerprint = $"{arch} {vtCommit} {zigVersion} " + ShaSum([.. InNative("GhosttyVt.c", "GhosttyClipboard.m"), Recipe]);
    const string view = "libGhosttyAvaloniaView.dylib", vt = "libGhosttyAvaloniaVt.dylib";
    if (Fresh("view", view, viewFingerprint) && Fresh("vt", vt, vtFingerprint))
    {
        Touch(Path.Combine(cache, view), Path.Combine(cache, vt));
        return 0;
    }
    // MSBuild may invoke this from multiple referencing projects, and other checkouts share the cache.
    using var buildLock = AcquireLock(Path.Combine(work, "build.lock"));

    var viewResult = Path.Combine(work, "results", "macos-view-" + CacheKey(viewFingerprint));
    if (!Fresh("view", view, viewFingerprint) && !File.Exists(Path.Combine(viewResult, view)))
    {
        Delete(viewResult + ".partial");
        Directory.CreateDirectory(viewResult + ".partial");
        var source = GhosttySource(work, "view-source", viewCommit);
        void Restore(params string[] paths)
        {
            foreach (var path in paths) GitShowToFile(source, viewCommit, path, Path.Combine(source, path));
        }
        Restore("src/renderer/message.zig", "src/renderer/Thread.zig", "src/renderer/generic.zig");
        ApplyPatch(source, Path.Combine(native, "Reclaim.patch"));
        ApplyPatch(source, Path.Combine(native, "LinkUnderlines.patch"));
        File.Copy(Path.Combine(native, "line-links.zig"), Path.Combine(source, "src", "renderer", "gav_line_links.zig"), true);
        Restore("src/renderer/Metal.zig", "src/renderer/metal/Frame.zig", "src/renderer/metal/Target.zig");
        ApplyPatch(source, Path.Combine(native, "MetalTexture.patch"));
        Restore("src/Surface.zig", "src/apprt/embedded.zig", "src/termio/backend.zig", "include/ghostty.h");
        ApplyPatch(source, Path.Combine(native, "ExternalIo.patch"));
        Restore("src/terminal/page.zig", "src/termio/stream_handler.zig");
        ApplyPatch(source, Path.Combine(native, "CommandEnd.patch"));
        Restore("src/config/url.zig");
        ApplyPatch(source, Path.Combine(native, "FileLinks.patch"));
        AppendFiles(Path.Combine(source, "src", "Surface.zig"), InNative("context-link.zig", "prompt-surface.zig", "reclaim-surface.zig"));
        AppendFiles(Path.Combine(source, "src", "apprt", "embedded.zig"), InNative("keyboard-mode.zig", "file-links.zig", "prompts.zig", "reclaim.zig"));
        var zig = ZigExe(zigVersion);
        RestoreZigDeps();
        Check(zig, ["test", "--cache-dir", Path.Combine(work, "view-cache"), Path.Combine(native, "line-links.zig")]);
        Check(zig, ["build", "--cache-dir", Path.Combine(work, "view-cache"), "-Doptimize=ReleaseFast", "-Drenderer=metal", "-Dapp-runtime=none",
            "-Demit-macos-app=false", "-Demit-docs=false", "-Demit-themes=false", "-Di18n=false", "-Dxcframework-target=native"], source);
        Check("clang", ["-dynamiclib", "-fobjc-arc", "-mmacosx-version-min=13.0", "-O2", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
            "-I", Path.Combine(source, "include"), Path.Combine(native, "GhosttyView.m"), Path.Combine(native, "GhosttyTexture.m"),
            Path.Combine(source, "macos", "GhosttyKit.xcframework", "macos-" + arch, "libghostty-internal.a"),
            "-framework", "AppKit", "-framework", "Metal", "-framework", "QuartzCore", "-framework", "IOSurface",
            "-framework", "CoreText", "-framework", "CoreGraphics", "-framework", "Carbon", "-framework", "Foundation", "-framework", "CoreVideo",
            "-lc++", "-install_name", "@rpath/libGhosttyAvaloniaView.dylib", "-o", Path.Combine(viewResult + ".partial", view)]);
        Directory.CreateDirectory(Path.Combine(viewResult + ".partial", "resources"));
        Copy(Path.Combine(source, "zig-out", "share", "terminfo"), Path.Combine(viewResult + ".partial", "resources", "terminfo"));
        PublishResult(viewResult);
    }
    if (!Fresh("view", view, viewFingerprint))
    {
        InstallOutput(viewResult, cache, view, "resources");
        File.WriteAllText(Path.Combine(cache, "view.fingerprint"), viewFingerprint + "\n");
    }

    var vtResult = Path.Combine(work, "results", "macos-vt-" + CacheKey(vtFingerprint));
    if (!Fresh("vt", vt, vtFingerprint) && !File.Exists(Path.Combine(vtResult, vt)))
    {
        Delete(vtResult + ".partial");
        Directory.CreateDirectory(vtResult + ".partial");
        var source = GhosttySource(work, "vt-source", vtCommit);
        var zig = ZigExe(zigVersion);
        RestoreZigDeps();
        Check(zig, ["build", "--cache-dir", Path.Combine(work, "vt-cache"), "--prefix", Path.Combine(work, "vt-out"), "-Demit-lib-vt", "-Doptimize=ReleaseFast"], source);
        Check("clang", ["-dynamiclib", "-fobjc-arc", "-mmacosx-version-min=13.0", "-O2", "-Wall", "-Wextra", "-Werror", "-DGHOSTTY_STATIC",
            "-I", Path.Combine(work, "vt-out", "include"), Path.Combine(native, "GhosttyVt.c"), Path.Combine(native, "GhosttyClipboard.m"),
            Path.Combine(work, "vt-out", "lib", "libghostty-vt.a"), "-framework", "AppKit",
            "-install_name", "@rpath/libGhosttyAvaloniaVt.dylib", "-o", Path.Combine(vtResult + ".partial", vt)]);
        PublishResult(vtResult);
    }
    if (!Fresh("vt", vt, vtFingerprint))
    {
        InstallOutput(vtResult, cache, vt);
        File.WriteAllText(Path.Combine(cache, "vt.fingerprint"), vtFingerprint + "\n");
    }
    // Whichever library was already current is not rebuilt; touching both tells MSBuild this run is done.
    Touch(Path.Combine(cache, view), Path.Combine(cache, vt));
    return 0;
});
