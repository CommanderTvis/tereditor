#!/usr/bin/env dotnet
// Builds the browser client's lazily downloaded userspace Tailscale node (Go -> js/wasm); no system Go installation is used.
// Output: this checkout's src/Tereditor.Browser/obj/tailscale-wasm, which the browser build deploys under `tailscale/` in the
// AppBundle: manifest.json (read first, never cached), and <version>-<build>/ with the module, its Brotli sidecar and Go's
// wasm_exec.js, plus licences. The finished result is cached machine-wide under $TEREDITOR_CACHE_DIR/tailscale-wasm/<key>.
// Usage: dotnet run --file scripts/build-tailscale-wasm.cs
#:include lib/Tasks.cs
#:include lib/Native.cs
#:include lib/Tailscale.cs
using System.IO.Compression;
using System.Text.Json;
using static Native;
using static Tasks;

return Guard(() =>
{
    var sourceDirectory = At("src", "Tereditor.Browser", "Tailscale.Wasm");
    var output = At("src", "Tereditor.Browser", "obj", "tailscale-wasm");
    var goroot = TailscaleGo.Provision();
    var tsversion = TailscaleGo.ModuleVersion(Path.Combine(sourceDirectory, "go.mod"));
    if (tsversion.Length == 0) throw new TaskFailure($"No tailscale.com version in {Path.Combine(sourceDirectory, "go.mod")}.");
    // Features a browser node cannot use or tereditor does not need; each ts_omit_ tag removes code from the module.
    var omit = """
        ace acme appconnectors aws advertiseexitnode advertiseroutes bird c2n cachenetmap captiveportal capture clientmetrics
        clientupdate cloud conn25 dbus debug debugeventbus debugportmapper dnsresolvecache doctor drive identityfederation kube
        linuxdnsfight linkspeed listenrawdisco logtail oauthkey outboundproxy osrouter peerapiclient peerapiserver portlist portmapper
        posture netlog networkmanager serviceclientprefs favorites qrcodes relayserver remoteconfig resolved routecheck runtimemetrics
        sdnotify serve ssh synology syslog syspolicy systray taildrop tap tpm tundevstats useroutes useexitnode useproxy usermetrics
        wakeonlan webbrowser webclient bakedroots completion cli colorable cliconndiag hujsonconf flashappliance androiddns iptables gro
        """;
    var tags = string.Join(',', omit.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(name => "ts_omit_" + name));
    var fingerprint = HashFiles(Path.Combine(sourceDirectory, "go.mod"), Path.Combine(sourceDirectory, "go.sum"), Path.Combine(sourceDirectory, "main.go"),
        Recipe, At("scripts", "lib", "Tailscale.cs"));
    var key = CacheKey(fingerprint);
    var build = $"{tsversion}-{key}";
    var cache = Path.Combine(CacheDir(), "tailscale-wasm");
    var result = Path.Combine(cache, key);
    Directory.CreateDirectory(cache);
    using (AcquireLock(Path.Combine(cache, key + ".lock")))
    {
        if (!File.Exists(Path.Combine(result, "manifest.json")))
        {
            var partial = result + ".partial";
            Delete(partial);
            Directory.CreateDirectory(Path.Combine(partial, build));
            Directory.CreateDirectory(Path.Combine(partial, "licenses"));
            TailscaleGo.UseCaches(goroot);
            Environment.SetEnvironmentVariable("GOOS", "js");
            Environment.SetEnvironmentVariable("GOARCH", "wasm");
            Environment.SetEnvironmentVariable("CGO_ENABLED", "0");
            var stamp = TailscaleGo.TereditorVersionStamp(tsversion);
            var go = TailscaleGo.Go(goroot);
            var module = Path.Combine(partial, build, "tereditor-tailscale.wasm");
            Check(go, ["build", "-mod=readonly", "-trimpath", "-tags", tags,
                $"-ldflags=-s -w -buildid= -X tailscale.com/version.shortStamp={stamp} -X tailscale.com/version.longStamp={stamp}", "-o", module, "."], sourceDirectory);
            var modules = TailscaleGo.Modules(goroot, sourceDirectory);
            File.Copy(Path.Combine(goroot, "lib", "wasm", "wasm_exec.js"), Path.Combine(partial, build, "wasm_exec.js"));
            File.Copy(Path.Combine(sourceDirectory, "TAILSCALE-LICENSE"), Path.Combine(partial, "licenses", "TAILSCALE-LICENSE"));
            File.Copy(Path.Combine(goroot, "LICENSE"), Path.Combine(partial, "licenses", "GO-LICENSE"));
            // One deterministic dependency licence bundle, module paths sorted bytewise, as the Android build does.
            using (var bundle = File.Create(Path.Combine(partial, "licenses", "THIRD-PARTY-NOTICES.txt")))
                foreach (var (name, directory) in modules.OrderBy(entry => entry.Module + "|" + entry.Directory, StringComparer.Ordinal))
                    foreach (var license in TailscaleGo.LicenseFiles(directory)) TailscaleGo.AppendNotice(bundle, $"{name}/{Path.GetFileName(license)}", license);
            foreach (var name in new[] { "tereditor-tailscale.wasm", "wasm_exec.js" })
            {
                var data = File.ReadAllBytes(Path.Combine(partial, build, name));
                var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
                if (!BrotliEncoder.TryCompress(data, compressed, out var written, 11, 22)) throw new TaskFailure($"Cannot compress {name}.");
                File.WriteAllBytes(Path.Combine(partial, build, name + ".br"), compressed[..written]);
            }
            string Json(string text) => JsonSerializer.Serialize(text);
            var size = new FileInfo(module).Length;
            var transferSize = new FileInfo(module + ".br").Length;
            File.WriteAllText(Path.Combine(partial, "manifest.json"),
                $"{{\"version\":{Json(tsversion)},\"build\":{Json(build)},\"module\":{Json($"tailscale/{build}/tereditor-tailscale.wasm")},\"exec\":{Json($"tailscale/{build}/wasm_exec.js")},\"size\":{size},\"transferSize\":{transferSize}}}");
            PublishResult(result);
        }
    }
    // Older builds' directories would otherwise accumulate in the checkout's output.
    Delete(output + ".partial");
    Copy(result, output + ".partial");
    Delete(output);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    Directory.Move(output + ".partial", output);
    Console.WriteLine(Path.Combine(output, "manifest.json"));
    return 0;
});
