#!/usr/bin/env dotnet
// Provisions what `--headscale` needs under $TEREDITOR_CACHE_DIR/headscale-test (the machine-wide cache): the checksum-pinned Headscale
// release binary, the userspace tailnet node used as the host side (cmd/tsforward) and the desktop
// build of the embedded Tailscale library the client side loads. No account, root or system Go.
// Usage: dotnet run --file scripts/build-headscale-test.cs
#:include lib/Tasks.cs
#:include lib/Native.cs
#:include lib/Tailscale.cs
using System.Runtime.InteropServices;
using static Native;
using static Tasks;

return Guard(() =>
{
    const string version = "0.29.5";
    var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    var intel = RuntimeInformation.ProcessArchitecture == Architecture.X64;
    var (platform, checksum) = (OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), arm, intel) switch
    {
        (true, _, true, _) => ("darwin_arm64", "e4f5ce794b7691a92d04a3dd9fb94ba259224021164c24bf01ad7dc83e8d76fe"),
        (true, _, _, true) => ("darwin_amd64", "6ddbcc54859cabfe13ac4ed6c481526251247165088d52622bc7bebc6240b8c1"),
        (_, true, true, _) => ("linux_arm64", "3afddd207b89ac22cfad4235cc163c6e538cc59ada139e7c0bf47640c5d9e945"),
        (_, true, _, true) => ("linux_amd64", "e979d27fe17d924d33bb6ab7504f6338d0635989a0cfdcfbc0ceb637dbcbe980"),
        _ => throw new TaskFailure("The Headscale check requires macOS or Linux, arm64 or x64."),
    };
    var output = Path.Combine(CacheDir(), "headscale-test");
    Directory.CreateDirectory(output);
    // Checkouts share the directory; each file is written beside it and renamed into place.
    using var installLock = AcquireLock(output + ".lock");
    if (ReadStamp(Path.Combine(output, "headscale.version")) != $"{version}-{platform}")
    {
        var download = Path.Combine(output, "headscale.download");
        Download($"https://github.com/juanfont/headscale/releases/download/v{version}/headscale_{version}_{platform}", download, 3);
        VerifySha256(download, checksum);
        MakeExecutable(download);
        File.Move(download, Path.Combine(output, "headscale"), true);
        File.WriteAllText(Path.Combine(output, "headscale.version"), $"{version}-{platform}\n");
    }
    var goroot = TailscaleGo.Provision();
    TailscaleGo.UseCaches(goroot);
    Environment.SetEnvironmentVariable("CGO_ENABLED", "1");
    var extension = OperatingSystem.IsMacOS() ? "dylib" : "so";
    var module = At("src", "Tereditor.Android", "Tailscale.Native");
    var go = TailscaleGo.Go(goroot);
    Check(go, ["build", "-mod=readonly", "-trimpath", "-o", Path.Combine(output, "tsforward.partial"), "./cmd/tsforward"], module);
    File.Move(Path.Combine(output, "tsforward.partial"), Path.Combine(output, "tsforward"), true);
    var library = Path.Combine(output, $"libTereditorTailscale.{extension}");
    Check(go, ["build", "-mod=readonly", "-trimpath", "-buildmode=c-shared", "-o", library + ".partial", "."], module);
    File.Move(library + ".partial", library, true);
    DeleteAll(Path.Combine(output, "libTereditorTailscale.h"), library + ".h");
    Console.WriteLine(output);
    return 0;
});
