#!/usr/bin/env dotnet
// Builds the app-private userspace Tailscale node; no system Go installation is used.
// Output: this checkout's src/Tereditor.Android/obj/tailscale/<abi>, which the Android project packages. The Go toolchain,
// build and module caches are the machine-wide ones scripts/tailscale-go.cs provisions; Go locks them itself.
// Usage: dotnet run --file scripts/build-tailscale-android.cs -- [arm64-v8a|x86_64]
#:include lib/Tasks.cs
#:include lib/Native.cs
#:include lib/Tailscale.cs
using static Tasks;

return Guard(() =>
{
    var abi = args.Length > 0 && args[0].Length > 0 ? args[0] : "arm64-v8a";
    var (goarch, compiler) = abi switch
    {
        "arm64-v8a" => ("arm64", "aarch64-linux-android24-clang"),
        "x86_64" => ("amd64", "x86_64-linux-android24-clang"),
        _ => throw new TaskFailure($"Unsupported Android ABI: {abi}"),
    };
    var output = At("src", "Tereditor.Android", "obj", "tailscale", abi);
    var goroot = TailscaleGo.Provision();
    Directory.CreateDirectory(output);
    var ndk = Env("ANDROID_NDK_HOME") ?? Tool("android-ndk");
    if (!Directory.Exists(Path.Combine(ndk, "toolchains"))) throw new TaskFailure($"No Android NDK at {ndk}.");
    var ndkHost = OperatingSystem.IsMacOS() ? "darwin-x86_64" : "linux-x86_64";
    TailscaleGo.UseCaches(goroot);
    Environment.SetEnvironmentVariable("GOOS", "android");
    Environment.SetEnvironmentVariable("GOARCH", goarch);
    Environment.SetEnvironmentVariable("CGO_ENABLED", "1");
    Environment.SetEnvironmentVariable("CC", Path.Combine(ndk, "toolchains", "llvm", "prebuilt", ndkHost, "bin", compiler));
    var module = At("src", "Tereditor.Android", "Tailscale.Native");
    // Library builds would report 1.x.y-devDATE-g<hash>-dirty to the admin console; stamp the pinned module version instead.
    var stamp = TailscaleGo.TereditorVersionStamp(TailscaleGo.ModuleVersion(Path.Combine(module, "go.mod")));
    var go = TailscaleGo.Go(goroot);
    Check(go, ["build", "-mod=readonly", "-trimpath", "-buildmode=c-shared",
        $"-ldflags=-s -w -buildid= -X tailscale.com/version.shortStamp={stamp} -X tailscale.com/version.longStamp={stamp} -extldflags=-Wl,-z,max-page-size=16384",
        "-o", Path.Combine(output, "libTereditorTailscale.so"), "."], module);
    // Module cache licenses are read-only. Keep staged copies writable so repeated builds replace them cleanly.
    TailscaleGo.CopyWritable(Path.Combine(module, "TAILSCALE-LICENSE"), Path.Combine(output, "TAILSCALE-LICENSE"));
    TailscaleGo.CopyWritable(Path.Combine(goroot, "LICENSE"), Path.Combine(output, "GO-LICENSE"));
    var licenses = Path.Combine(output, "licenses");
    foreach (var (name, directory) in TailscaleGo.Modules(goroot, module))
    {
        var destination = Path.Combine(licenses, name);
        Directory.CreateDirectory(destination);
        foreach (var license in TailscaleGo.LicenseFiles(directory)) TailscaleGo.CopyWritable(license, Path.Combine(destination, Path.GetFileName(license)));
    }
    // A constant-name bundle avoids Android asset wildcard metadata differences.
    // License paths identify modules; bytewise sorting keeps the bundle deterministic.
    var files = Directory.EnumerateFiles(licenses, "*", SearchOption.AllDirectories)
        .Select(file => (Relative: Path.GetRelativePath(licenses, file).Replace('\\', '/'), Path: file)).OrderBy(file => file.Relative, StringComparer.Ordinal);
    using (var bundle = File.Create(Path.Combine(output, "THIRD-PARTY-NOTICES.txt")))
        foreach (var file in files) TailscaleGo.AppendNotice(bundle, file.Relative, file.Path);
    Console.WriteLine(Path.Combine(output, "libTereditorTailscale.so"));
    return 0;
});
