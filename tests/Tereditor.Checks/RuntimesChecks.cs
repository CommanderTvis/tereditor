using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;
using Tereditor.Host.Core.Language;
using Tereditor.Host.Core.Plugins;
using Tereditor.Plugins.Acp;
using Tereditor.Plugins.Acp.Host;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Runtimes;
using Tereditor.Plugins.Runtimes.Host;

namespace Tereditor.Checks;

// The Runtimes for tooling plugin (Plugins.Runtimes/SPEC.md): the order pinned, system, downloaded; no download while the plugin is off, with the message that offers it;
// the uv and Bun guards; and what turning plugins off deletes (only when the user does it, never when tereditor quits). Fake downloads, no network.
internal static class RuntimesChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FakeSource : HttpMessageHandler
    {
        internal int Requests;
        internal readonly Dictionary<string, byte[]> Bodies = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            var body = Bodies.First(pair => request.RequestUri!.AbsolutePath.Contains(pair.Key, StringComparison.Ordinal)).Value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }

    // A tar.gz with one top folder and an executable stub, like the real uv and Bun archives.
    private static byte[] Archive(string folder, string tool)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, folder + "/") { Mode = (UnixFileMode)0x1ed });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, folder + "/" + tool) { Mode = (UnixFileMode)0x1ed, DataStream = new MemoryStream("#!/bin/sh\necho fake 1.2.3\n"u8.ToArray()) });
        }
        return output.ToArray();
    }

    private static ManagedRuntimePin Pin(string id, byte[] archive) =>
        new(id, "9.9.9", ManagedArtifacts.Platform(), $"https://fake.test/{id}-9.9.9.tar.gz", Convert.ToHexStringLower(SHA256.HashData(archive)), "", "tar.gz", "check fixture", ["fake.test"]);

    private static string Tool(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\necho 1.0.0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)0x1ed);
        return path;
    }

    private static void Env(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    internal static async Task Run(string fixture)
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP runtimes checks use shell stubs and POSIX processes"); return; }
        var root = Path.Combine(fixture, "runtimes");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var saved = new[] { Toolchains.SourceVariable, Toolchains.CacheVariable, Toolchains.OfflineVariable, "PATH", "JAVA_HOME" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Env(Toolchains.SourceVariable, null); Env(Toolchains.CacheVariable, null); Env(Toolchains.OfflineVariable, null);
        try
        {
            await Resolution(root);
            await Verification(root);
            await Consent(root);
            await Jdk(root);
            await Disabling(root);
        }
        finally { foreach (var (name, value) in saved) Env(name, value); ManagedRuntimes.UsePins(null); }
    }

    // The pins ship with a SHA-256 for every platform a runtime supports; a damaged download is refused and never used; the verified record follows the file.
    private static async Task Verification(string root)
    {
        ManagedRuntimes.UsePins(null);
        string[] all = ["linux-aarch64", "linux-x86_64", "macos-aarch64", "macos-x86_64", "windows-aarch64", "windows-x86_64"];
        foreach (var (id, platforms) in new[] { ("bun", all), ("uv", all), ("jdk", all.Where(item => item != "windows-aarch64").ToArray()) })
        {
            var pins = ManagedRuntimes.Pins.Where(pin => pin.Id == id).ToArray();
            Require(pins.Select(pin => pin.Platform).Order().SequenceEqual(platforms), $"{id} is not pinned for exactly {string.Join(", ", platforms)}.");
            Require(pins.All(pin => pin.Sha256.Length == 64 && pin.Sha256.All(char.IsAsciiHexDigitLower) && pin.Url.StartsWith("https://", StringComparison.Ordinal) && pin.Url.Contains(pin.Version, StringComparison.Ordinal)), $"A {id} pin lacks a 64-hex SHA-256.");
        }
        Console.WriteLine("PASS Bun, uv and Temurin JDK 21 each have a 64-hex SHA-256 for every supported platform");

        var archive = Archive("bun-fake", "bun");
        ManagedRuntimes.UsePins([Pin("bun", archive)]);
        var source = new FakeSource { Bodies = { ["bun-"] = archive } };
        using var http = new HttpClient(source);
        var workspace = Path.Combine(root, "verify-workspace");
        Directory.CreateDirectory(workspace);
        var withTools = Path.Combine(root, "verify-bin-with");
        var systemBun = Tool(withTools, "bun");
        var without = Path.Combine(root, "verify-bin-without");
        Directory.CreateDirectory(without);
        var directory = Path.Combine(root, "verify");
        var servers = new LanguageServers(directory, null, http);
        Env("PATH", without + ":/usr/bin:/bin");
        var first = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(first is { Available: true, Source: RuntimeSource.Downloaded }, "The fixture download did not install.");
        var program = first.Command;
        var record = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(program))!, ".tereditor-verified.json");
        var good = File.ReadAllBytes(program);
        void Write(byte[] bytes)
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(program, (UnixFileMode)0x1ff);
            File.WriteAllBytes(program, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(program, (UnixFileMode)0x1ed);
        }
        Require(File.Exists(record) && File.ReadAllText(record).Contains(Convert.ToHexStringLower(SHA256.HashData(good)), StringComparison.Ordinal), "The installer did not record the program's hash.");

        // A touched file that still hashes the same is accepted and the record follows its new time; a changed file is refused.
        var before = File.ReadAllText(record);
        File.SetLastWriteTimeUtc(program, DateTime.UtcNow.AddHours(-3));
        Require((await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default)).Source == RuntimeSource.Downloaded && File.ReadAllText(record) != before, "A new modified time with the same bytes must be hashed, accepted and recorded again.");
        var recorded = File.ReadAllText(record);
        Require((await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default)).Source == RuntimeSource.Downloaded && File.ReadAllText(record) == recorded, "An unchanged file must be trusted without a new record.");
        Write("#!/bin/sh\necho evil\n"u8.ToArray());
        var bad = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(!bad.Available && bad.Source is null && bad.Message.Contains("checksum", StringComparison.Ordinal) && bad.Message.Contains("Remove the download", StringComparison.Ordinal) && source.Requests == 1, "A corrupted download must be refused with a clear message and not downloaded again: " + bad.Message);
        Require(ManagedRuntimes.FindInstalled(directory, "bun", ManagedArtifacts.Platform(), workspace) is null, "A corrupted download was still found.");
        var status = await servers.StatusAsync(RuntimeKind.Bun, default);
        Require(status is { Source: null, Downloaded: true } && status.DownloadProblem.Contains("checksum", StringComparison.Ordinal) && status.Message.Length > 0, "The status must show the failed check on the row.");

        Env("PATH", withTools + ":/usr/bin:/bin");
        var fallback = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(fallback is { Available: true, Source: RuntimeSource.System } && fallback.Command == systemBun, "With a corrupted download the computer's Bun is used.");
        status = await servers.StatusAsync(RuntimeKind.Bun, default);
        Require(status is { Source: RuntimeSource.System } && status.DownloadProblem.Length > 0, "The fallback must still say why the download is not used.");
        Require(servers.ProbeTool("bun", workspace, "", true) is { State: "found", Source: "path" }, "A language server must not start from a corrupted download.");
        Write(good);
        File.SetLastWriteTimeUtc(program, DateTime.UtcNow.AddHours(-1));
        Require((await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default)).Source == RuntimeSource.Downloaded, "The repaired file must be accepted again.");

        // A record that names another pin, or no record, is not trusted.
        File.WriteAllText(record, File.ReadAllText(record).Replace(Convert.ToHexStringLower(SHA256.HashData(archive)), new string('0', 64), StringComparison.Ordinal));
        Require((await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default)).Source != RuntimeSource.Downloaded, "A record that does not match the pinned checksum was trusted.");
        File.Delete(record);
        Require((await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default)).Source == RuntimeSource.System, "A copy without a verification record was trusted.");
        servers.RemoveRuntimeDownload(RuntimeKind.Bun);
        Console.WriteLine("PASS a corrupted download is refused and never used, the verified record follows the file, and the computer's copy is the fallback");
    }

    // The question before a plugin's request downloads: asked once per runtime, remembered No for the session, never asked when the runtime is already there.
    private static async Task Consent(string root)
    {
        var bunArchive = Archive("bun-fake", "bun");
        var uvArchive = Archive("uv-fake", "uv");
        ManagedRuntimes.UsePins([Pin("bun", bunArchive), Pin("uv", uvArchive)]);
        var source = new FakeSource { Bodies = { ["bun-"] = bunArchive, ["uv-"] = uvArchive } };
        using var http = new HttpClient(source);
        var workspace = Path.Combine(root, "consent-workspace");
        Directory.CreateDirectory(workspace);
        var without = Path.Combine(root, "consent-bin-without");
        Directory.CreateDirectory(without);
        var withTools = Path.Combine(root, "consent-bin-with");
        Tool(withTools, "bun");

        // The host side: a Not now means no request and a message that says where to download; a Download downloads once.
        Env("PATH", withTools + ":/usr/bin:/bin");
        var asked = new List<RuntimeOffer>();
        var yes = false;
        var servers = new LanguageServers(Path.Combine(root, "consent"), null, http) { RuntimeConsent = (offer, _) => { asked.Add(offer); return ValueTask.FromResult(yes); } };
        Require((await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default)).Source == RuntimeSource.System && asked.Count == 0 && source.Requests == 0, "A runtime found on PATH was asked about or downloaded.");
        Env("PATH", without + ":/usr/bin:/bin");
        var declined = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(!declined.Available && declined.Message.Contains("Not now", StringComparison.Ordinal) && declined.Message.Contains("Settings", StringComparison.Ordinal) && !declined.Message.Contains((char)8212)
            && asked is [{ Kind: RuntimeKind.Bun, Name: "Bun" }] && asked[0].Version.Length > 0 && source.Requests == 0, "A Not now must download nothing and say where to download: " + declined.Message);
        yes = true;
        var accepted = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(accepted is { Available: true, Source: RuntimeSource.Downloaded } && asked.Count == 2 && source.Requests == 1, "A yes downloads the runtime once.");
        await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(asked.Count == 2 && source.Requests == 1, "A runtime that is installed is not asked about again.");
        Console.WriteLine("PASS a missing runtime asks before it downloads; Not now downloads nothing; PATH and installed copies are never asked about");

        // The plugin side: one question for concurrent requests, a remembered No, Download now forgets it, closing answers No.
        var published = new List<RuntimeConsents>();
        var broker = new RuntimeConsentBroker(published.Add);
        var offer = new RuntimeOffer(RuntimeKind.Bun, "Bun", "1.4.4");
        var first = broker.AskAsync(offer, default).AsTask();
        var second = broker.AskAsync(offer, default).AsTask();
        Require(published.Count == 1 && published[0].Pending is [{ Kind: "bun", Name: "Bun", Version: "1.4.4" }] && broker.Snapshot().Pending.Count == 1, "Two requests for one runtime must share one question.");
        var other = broker.AskAsync(new(RuntimeKind.Uv, "uv", "0.13.9"), default).AsTask();
        Require(published.Count == 2 && published[1].Pending.Count == 2, "A second runtime gets its own question.");
        broker.Answer(RuntimeKind.Bun, false);
        Require(await first == false && await second == false && broker.Snapshot().Pending is [{ Kind: "uv" }] && published[^1].Pending is [{ Kind: "uv" }], "A No answers every waiting request and takes the question down.");
        Require(!await broker.AskAsync(offer, default) && published.Count == 3, "After Not now the runtime is not asked about again in the session.");
        broker.Forget(RuntimeKind.Bun);
        var again = broker.AskAsync(offer, default).AsTask();
        broker.Answer(RuntimeKind.Bun, true);
        Require(await again, "After Download now the question comes back and a yes downloads.");
        broker.Close();
        Require(!await other && broker.Snapshot().Pending.Count == 0, "Closing the plugin answers open questions with No.");
        Console.WriteLine("PASS one question per runtime, a remembered Not now, and Download now clears it");
    }

    // A tar.gz like Temurin's: one top folder with the JDK's files, the macOS layout when home is "Contents/Home".
    private static byte[] JdkArchive(string home, int major)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, true))
        {
            foreach (var folder in new[] { "jdk-fake/", $"jdk-fake/{home}/", $"jdk-fake/{home}/bin/" }.Select(path => path.Replace("//", "/")))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, folder) { Mode = (UnixFileMode)0x1ed });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"jdk-fake/{home}/bin/java") { Mode = (UnixFileMode)0x1ed, DataStream = new MemoryStream("#!/bin/sh\necho openjdk 21.0.13\n"u8.ToArray()) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"jdk-fake/{home}/release") { Mode = (UnixFileMode)0x1a4, DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"JAVA_VERSION=\"{major}.0.13\"\n")) });
        }
        return output.ToArray();
    }

    // A JDK the user might have: bin/java beside a release file.
    private static string Home(string directory, string version)
    {
        Directory.CreateDirectory(Path.Combine(directory, "bin"));
        File.WriteAllText(Path.Combine(directory, "release"), $"JAVA_VERSION=\"{version}\"\n");
        return Tool(Path.Combine(directory, "bin"), "java");
    }

    private static async Task Jdk(string root)
    {
        // The shipped pins: Temurin 21 for the five platforms the Adoptium API serves, one version, a SHA-256 each, GitHub release hosts only.
        ManagedRuntimes.UsePins(null);
        var shipped = ManagedRuntimes.Pins.Where(pin => pin.Id == "jdk").ToArray();
        Require(shipped.Select(pin => pin.Platform).Order().SequenceEqual(["linux-aarch64", "linux-x86_64", "macos-aarch64", "macos-x86_64", "windows-x86_64"]) && shipped.Select(pin => pin.Version).Distinct().Count() == 1 && shipped[0].Version.StartsWith("21.", StringComparison.Ordinal), "The JDK is not pinned at one 21 version for exactly the five platforms.");
        Require(shipped.All(pin => pin.Url.StartsWith("https://github.com/adoptium/temurin21-binaries/releases/download/", StringComparison.Ordinal) && pin.Sha256.Length == 64 && pin.Hosts!.Contains(new Uri(pin.Url).Host)) && shipped.Select(pin => pin.Sha256).Distinct().Count() == shipped.Length, "A JDK pin lacks its SHA-256 or leaves Temurin's releases.");
        var layout = Path.Combine(root, "jdk-layout");
        var kind = ManagedRuntimes.KindOf("java")!;
        Directory.CreateDirectory(Path.Combine(layout, "flat", "jdk-21", "bin"));
        Directory.CreateDirectory(Path.Combine(layout, "mac", "jdk-21", "Contents", "Home", "bin"));
        Require(ManagedRuntimes.BinDirectoryOf(Path.Combine(layout, "flat"), kind) == Path.Combine(layout, "flat", "jdk-21", "bin") && ManagedRuntimes.HomeOf(Path.Combine(layout, "flat"), kind) == Path.Combine(layout, "flat", "jdk-21")
            && ManagedRuntimes.BinDirectoryOf(Path.Combine(layout, "mac"), kind) == Path.Combine(layout, "mac", "jdk-21", "Contents", "Home", "bin") && ManagedRuntimes.HomeOf(Path.Combine(layout, "mac"), kind) == Path.Combine(layout, "mac", "jdk-21", "Contents", "Home"), "A JDK's bin and home are not found in the Linux, Windows and macOS layouts.");
        Console.WriteLine("PASS the Temurin JDK is pinned for five platforms with a SHA-256 and found in its Linux and macOS layouts");

        var archive = JdkArchive("Contents/Home", 21);
        ManagedRuntimes.UsePins([Pin("jdk", archive)]);
        var source = new FakeSource { Bodies = { ["jdk-"] = archive } };
        using var http = new HttpClient(source);
        var workspace = Path.Combine(root, "jdk-workspace");
        Directory.CreateDirectory(workspace);
        var empty = Path.Combine(root, "jdk-empty-path");
        Directory.CreateDirectory(empty);
        var java = LanguageServers.Catalog.Single(server => server.Id == "java");
        var metals = LanguageServers.Catalog.Single(server => server.Id == "scala");
        var old = Home(Path.Combine(root, "jdk-17"), "17.0.9");
        var current = Home(Path.Combine(root, "jdk-21"), "21.0.2");
        Env("PATH", empty);
        Env("JAVA_HOME", null);

        // jdtls needs 21, Metals takes 11 or newer: a JDK of the user's that suits the server is used and nothing is downloaded.
        var servers = new LanguageServers(Path.Combine(root, "jdk"), null, http);
        Env("JAVA_HOME", Path.GetDirectoryName(Path.GetDirectoryName(old)));
        Require(servers.ProbeTool("java", workspace, "JAVA_HOME", true, 11) is { State: "found", Source: "home" } && servers.ProbeTool("java", workspace, "JAVA_HOME", true, 21) is { State: "download", Source: "managed" }, "A JDK 17 must suit Metals and be passed over for jdtls.");
        Env("JAVA_HOME", Path.GetDirectoryName(Path.GetDirectoryName(current)));
        Require(servers.ProbeTool("java", workspace, "JAVA_HOME", true, 21) is { State: "found", Source: "home" } && source.Requests == 0, "A JDK 21 of the user's must be used and nothing downloaded.");
        Env("JAVA_HOME", null);
        Env("PATH", Path.GetDirectoryName(old));
        Require(servers.ProbeTool("java", workspace, "JAVA_HOME", true, 11) is { State: "found", Source: "path" } && servers.ProbeTool("java", workspace, "JAVA_HOME", true, 21) is { State: "download" }, "The same must hold for a JDK on PATH.");
        Console.WriteLine("PASS a JDK of the user's is used when it suits the server (jdtls 21, Metals 11) and passed over when it is older");

        // Nothing suitable and the plugin on: the JDK is downloaded once, checked, and both servers launch with JAVA_HOME naming it.
        Env("PATH", empty);
        var provided = await servers.ProvideToolAsync("java", workspace, default, 21);
        Require(provided is not null && provided.StartsWith(Path.Combine(root, "jdk", "runtimes", "jdk"), StringComparison.Ordinal) && provided.EndsWith(Path.Combine("Contents", "Home", "bin", "java"), StringComparison.Ordinal) && source.Requests == 1, "The JDK was not downloaded into the cache: " + provided);
        var home = Path.GetDirectoryName(Path.GetDirectoryName(provided))!;
        foreach (var server in new[] { java, metals })
        {
            var start = LanguageServers.Start("fixture", []);
            servers.ConfigureCaches(start, server, workspace);
            Require(start.Environment["JAVA_HOME"] == home && start.Environment["PATH"]!.StartsWith(Path.GetDirectoryName(provided)! + ":", StringComparison.Ordinal), $"{server.Id} does not launch with JAVA_HOME naming the downloaded JDK: {start.Environment["JAVA_HOME"]}");
        }
        var resolved = await servers.ResolveRuntimeAsync(RuntimeKind.Jdk, workspace, default);
        Require(resolved is { Available: true, Source: RuntimeSource.Downloaded } && resolved.Command == provided && resolved.Args.Count == 0 && source.Requests == 1, "The JDK runtime does not resolve to the downloaded copy.");
        var status = await servers.StatusAsync(RuntimeKind.Jdk, default);
        Require(status is { Downloaded: true, Source: RuntimeSource.Downloaded } && status.Name == "Temurin JDK 21" && status.DownloadBytes > 0, "Settings does not show the downloaded JDK.");
        Console.WriteLine("PASS an older or missing JDK is replaced by one download, and jdtls and Metals launch with JAVA_HOME naming it");

        // A JDK the user chose is never replaced: not the one in JAVA_HOME that suits, not the path set in Settings.
        Env("JAVA_HOME", Path.GetDirectoryName(Path.GetDirectoryName(current)));
        var kept = LanguageServers.Start("fixture", []);
        servers.ConfigureCaches(kept, java, workspace);
        Require(kept.Environment["JAVA_HOME"] == Path.GetDirectoryName(Path.GetDirectoryName(current)), "A suitable JAVA_HOME of the user's was replaced.");
        Env("JAVA_HOME", Path.GetDirectoryName(Path.GetDirectoryName(old)));
        servers.RuntimePin = name => name == "java" ? old : "";
        var chosen = LanguageServers.Start("fixture", []);
        servers.ConfigureCaches(chosen, java, workspace);
        Require(servers.ProbeTool("java", workspace, "JAVA_HOME", true, 21) is { State: "found", Source: "override" } && chosen.Environment["JAVA_HOME"] == Path.GetDirectoryName(Path.GetDirectoryName(old)), "A JDK path the user set was replaced.");
        servers.RuntimePin = _ => "";
        Console.WriteLine("PASS a JDK the user set or exported is never replaced");

        // The plugin off: no download, the host's older JDK is kept as before, and with none the answer offers to turn the plugin on.
        var off = new LanguageServers(Path.Combine(root, "jdk-off"), null, http) { RuntimesEnabled = () => false };
        var before = source.Requests;
        Require(off.ProbeTool("java", workspace, "JAVA_HOME", true, 21) is { State: "found", Source: "home" }, "With the plugin off the user's older JDK is not kept.");
        Env("JAVA_HOME", null);
        Env("PATH", empty);
        Require(off.ProbeTool("java", workspace, "JAVA_HOME", true, 21) is { State: "missing" } missing && missing.Problem.Contains("Runtimes for tooling", StringComparison.Ordinal) && source.Requests == before, "With the plugin off and no JDK, nothing may download and the answer offers the plugin.");
        Console.WriteLine("PASS with the plugin off no JDK is downloaded and the answer offers to turn it on");
    }

    private static async Task Resolution(string root)
    {
        var bunArchive = Archive("bun-fake", "bun");
        var uvArchive = Archive("uv-fake", "uv");
        ManagedRuntimes.UsePins([Pin("bun", bunArchive), Pin("uv", uvArchive)]);
        var source = new FakeSource { Bodies = { ["bun-"] = bunArchive, ["uv-"] = uvArchive } };
        using var http = new HttpClient(source);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var withTools = Path.Combine(root, "bin-with");
        var systemBun = Tool(withTools, "bun");
        var systemUv = Tool(withTools, "uv");
        var without = Path.Combine(root, "bin-without");
        Directory.CreateDirectory(without);
        var directory = Path.Combine(root, "resolve");

        // System: no pin, a program on PATH.
        Env("PATH", withTools + ":/usr/bin:/bin");
        var servers = new LanguageServers(directory, null, http);
        var bun = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        var uv = await servers.ResolveRuntimeAsync(RuntimeKind.Uv, workspace, default);
        Require(bun is { Available: true, Source: RuntimeSource.System } && bun.Command == systemBun && bun.Args.SequenceEqual(["x"]), "A Bun on PATH is the system Bun, run as bun x.");
        Require(uv is { Available: true, Source: RuntimeSource.System } && uv.Command == systemUv && uv.Args.SequenceEqual(["tool", "run"]), "A uv on PATH is the system uv, run as uv tool run.");
        Require(source.Requests == 0, "A runtime on PATH was downloaded anyway.");
        Console.WriteLine("PASS a program on PATH is used and nothing is downloaded");

        // The guards: Bun's config and .env, uv's config and .env, whatever the source.
        Require(bun.Env["BUN_OPTIONS"].Contains("--no-env-file", StringComparison.Ordinal) && bun.Env["BUN_OPTIONS"].Contains("empty-bunfig.toml", StringComparison.Ordinal)
            && new FileInfo(Path.Combine(directory, "bun-home", "empty-bunfig.toml")).Length == 0 && bun.Env["BUN_INSTALL"] == Path.Combine(directory, "bun-home"), "Bun was not kept from the workspace's bunfig.toml and .env.");
        Require(uv.Env["UV_NO_CONFIG"] == "1" && uv.Env["UV_NO_ENV_FILE"] == "1" && !uv.Env.ContainsKey("UV_CACHE_DIR"), "uv was not kept from the workspace's uv.toml, pyproject.toml and .env, or a system uv lost its own cache.");
        Console.WriteLine("PASS Bun and uv ignore configuration and .env files in the working directory");

        // Pinned beats system; a pin that cannot run is said so, not replaced.
        var pinned = Tool(Path.Combine(root, "chosen"), "bun");
        servers.RuntimePin = name => name == "bun" ? pinned : "";
        bun = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(bun is { Available: true, Source: RuntimeSource.Pinned } && bun.Command == pinned, "The path the user pinned must win over PATH.");
        servers.RuntimePin = name => name == "bun" ? Path.Combine(root, "missing", "bun") : "";
        bun = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(!bun.Available && bun.Message.Contains("path you set", StringComparison.Ordinal) && !bun.OfferEnable && source.Requests == 0, "A pin that cannot run is reported and nothing else is used in its place.");
        servers.RuntimePin = _ => "";
        Console.WriteLine("PASS the pinned path comes first, and a broken pin is reported");

        // Downloaded: only when nothing else is there, once, checked against the pin.
        Env("PATH", without + ":/usr/bin:/bin");
        bun = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        uv = await servers.ResolveRuntimeAsync(RuntimeKind.Uv, workspace, default);
        Require(bun is { Available: true, Source: RuntimeSource.Downloaded } && bun.Command.StartsWith(Path.Combine(directory, "runtimes", "bun"), StringComparison.Ordinal) && File.Exists(bun.Command), "Bun was not downloaded into the cache.");
        Require(uv is { Available: true, Source: RuntimeSource.Downloaded } && uv.Command.StartsWith(Path.Combine(directory, "runtimes", "uv"), StringComparison.Ordinal), "uv was not downloaded into the cache.");
        Require(source.Requests == 2, "Each runtime downloads once.");
        await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(source.Requests == 2, "A second request downloaded again.");
        var uvHome = Path.Combine(directory, "uv-home");
        Require(uv.Env["UV_CACHE_DIR"] == Path.Combine(uvHome, "cache") && uv.Env["UV_TOOL_DIR"] == Path.Combine(uvHome, "tools") && uv.Env["UV_PYTHON_INSTALL_DIR"] == Path.Combine(uvHome, "python") && uv.Env["UV_NO_CONFIG"] == "1",
            "A downloaded uv keeps its cache, tools and Pythons in the cache folder it is removed with, and ignores configuration files.");
        Console.WriteLine("PASS the downloaded copy is the last choice, downloads once and keeps its caches in one place");

        // Once downloaded the copy stays preferred: a program on PATH no longer outranks it.
        Env("PATH", withTools + ":/usr/bin:/bin");
        bun = await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        Require(bun is { Available: true, Source: RuntimeSource.Downloaded } && bun.Command.StartsWith(Path.Combine(directory, "runtimes", "bun"), StringComparison.Ordinal) && source.Requests == 2, "A downloaded Bun must outrank the one on PATH.");
        var shown = await servers.StatusAsync(RuntimeKind.Bun, default);
        Require(shown is { Source: RuntimeSource.Downloaded, DownloadProblem: "", OtherPath: var other } && other == systemBun && shown.Version == "9.9.9", "The status must say the download is in use and the computer's Bun is beside it.");
        servers.RuntimePin = name => name == "bun" ? pinned : "";
        Require((await servers.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default)) is { Source: RuntimeSource.Pinned }, "The path the user set must still outrank the download.");
        servers.RuntimePin = _ => "";
        Console.WriteLine("PASS a downloaded copy outranks the one on PATH; the pinned path outranks both");
        Env("PATH", without + ":/usr/bin:/bin");

        // Plugin off: no download, a message that offers to turn it on; a program that is already there still works.
        var fresh = new LanguageServers(Path.Combine(root, "resolve-off"), null, http) { RuntimesEnabled = () => false };
        var before = source.Requests;
        bun = await fresh.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default);
        uv = await fresh.ResolveRuntimeAsync(RuntimeKind.Uv, workspace, default);
        Require(!bun.Available && bun.OfferEnable && bun.Message.Contains("Bun", StringComparison.Ordinal) && bun.Message.Contains("Runtimes for tooling", StringComparison.Ordinal) && bun.Message.Contains("Settings", StringComparison.Ordinal), "Off and missing, Bun's answer must name Bun and offer to turn Runtimes for tooling on: " + bun.Message);
        Require(!uv.Available && uv.OfferEnable && uv.Message.Contains("uv", StringComparison.Ordinal) && source.Requests == before, "Off and missing, uv's answer offers the plugin and nothing is downloaded.");
        var probe = fresh.ProbeTool("bun", workspace);
        Require(probe is { State: "missing" } && probe.Problem.Contains("Runtimes for tooling", StringComparison.Ordinal), "A language server that needs Bun must be told that the plugin is off.");
        var installed = new LanguageServers(directory, null, http) { RuntimesEnabled = () => false };
        Require(!(await installed.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default)).Available, "Off, a downloaded copy that is still there is not used.");
        Env("PATH", withTools + ":/usr/bin:/bin");
        Require((await fresh.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, default)) is { Available: true, Source: RuntimeSource.System }, "Off, a Bun on PATH is still used.");
        Console.WriteLine("PASS with the plugin off nothing is downloaded and the answer offers to turn it on");
    }

    private static async Task Disabling(string root)
    {
        var bunArchive = Archive("bun-fake", "bun");
        var uvArchive = Archive("uv-fake", "uv");
        var jdkArchive = JdkArchive("Contents/Home", 21);
        ManagedRuntimes.UsePins([Pin("bun", bunArchive), Pin("uv", uvArchive), Pin("jdk", jdkArchive)]);
        var source = new FakeSource { Bodies = { ["bun-"] = bunArchive, ["uv-"] = uvArchive, ["jdk-"] = jdkArchive } };
        using var http = new HttpClient(source);
        var directory = Path.Combine(root, "disable");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(root, "bin-without"));
        Env("PATH", Path.Combine(root, "bin-without") + ":/usr/bin:/bin");
        var state = new HostStateStore(directory);
        var cache = state.LanguageDirectory;
        foreach (var pin in ManagedRuntimes.Pins) await ManagedRuntimes.EnsureAsync(cache, pin, http, null, false, default);
        string Touch(params string[] parts)
        {
            var file = Path.Combine([cache, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
            return file;
        }
        var bunCache = Touch("bun-home", "install", "cache", "pkg");
        var uvCache = Touch("uv-home", "cache", "wheel");
        var uvTool = Touch("uv-home", "tools", "agent");
        var shellInstall = Touch("servers", "shell", "1.0", "server.js");
        var shellCache = Touch("cache", "shell", "1.0", "index");
        var shellWorkspace = Touch("workspaces", "key", "shell", "state");
        var cssInstall = Touch("servers", "css", "1.0", "server.js");
        var bunProgram = ManagedRuntimes.FindInstalled(cache, "bun", ManagedArtifacts.Platform(), cache)!;
        using var downloadedUser = Process.Start(new ProcessStartInfo("sleep", "600") { UseShellExecute = false }) ?? throw new InvalidOperationException("sleep did not start.");
        using var systemUser = Process.Start(new ProcessStartInfo("sleep", "600") { UseShellExecute = false }) ?? throw new InvalidOperationException("sleep did not start.");
        try
        {
            LanguageService.TrackRuntimeProcess(new(RuntimeKind.Bun, true, bunProgram, ["x"], new Dictionary<string, string>(), RuntimeSource.Downloaded, "", false), downloadedUser.Id);
            LanguageService.TrackRuntimeProcess(new(RuntimeKind.Bun, true, "/usr/local/bin/bun", ["x"], new Dictionary<string, string>(), RuntimeSource.System, "", false), systemUser.Id);

            // tereditor quitting deletes nothing and stops nothing of the runtimes.
            await using (var quitting = new PluginRuntime(new PluginHostSeams { StateDirectory = directory, State = state, Builtins = Plugins() }))
            {
                await quitting.Start();
                await state.ChangeAsync([HostStateChange.PluginSettings(RuntimesPlugin.Id, """{"bunPath":"/pinned/bun"}""")]);
                var pin = await state.Languages.ResolveRuntimeAsync(RuntimeKind.Bun, directory, default);
                Require(!pin.Available && pin.Message.Contains("path you set", StringComparison.Ordinal), "The path pinned in the plugin's settings is not read by the host: " + pin.Message);
            }
            Require(File.Exists(bunCache) && File.Exists(uvCache) && File.Exists(uvTool) && File.Exists(shellInstall) && File.Exists(shellCache) && File.Exists(shellWorkspace) && File.Exists(bunProgram) && !downloadedUser.HasExited,
                "Quitting tereditor deleted downloads or stopped a process.");
            Console.WriteLine("PASS quitting tereditor keeps every download and process");

            await using var runtime = new PluginRuntime(new PluginHostSeams { StateDirectory = directory, State = state, Builtins = Plugins() });
            await runtime.Start();
            async Task Set(string id, bool on) { await state.ChangeAsync([HostStateChange.PluginEnabled(id, on)]); await runtime.Schedule(); }

            // A language server plugin turned off by the user removes what was downloaded for its server, and nothing of another.
            await Set("lsp-shell", false);
            Require(!File.Exists(shellInstall) && !File.Exists(shellCache) && !File.Exists(shellWorkspace) && !Directory.Exists(Path.Combine(cache, "servers", "shell")), "Turning the Shell plugin off left its downloads.");
            Require(File.Exists(cssInstall) && File.Exists(bunCache) && File.Exists(bunProgram), "Turning one language server off removed another server's or the runtimes' files.");
            Console.WriteLine("PASS turning a language server plugin off deletes its downloads and only those");

            // Runtimes for tooling turned off: its processes stop, everything it downloaded goes, the pin stays.
            await Set(RuntimesPlugin.Id, false);
            Require(downloadedUser.WaitForExit(10000), "A process running from the downloaded Bun was left running.");
            Require(!systemUser.HasExited, "A process running from a system Bun was stopped.");
            Require(!File.Exists(bunCache) && !File.Exists(uvCache) && !File.Exists(uvTool) && !File.Exists(bunProgram) && !Directory.Exists(Path.Combine(cache, "runtimes", "bun")) && !Directory.Exists(Path.Combine(cache, "runtimes", "uv")) && !Directory.Exists(Path.Combine(cache, "runtimes", "jdk"))
                && !Directory.Exists(Path.Combine(cache, "bun-home")) && !Directory.Exists(Path.Combine(cache, "uv-home")), "Turning Runtimes for tooling off left downloads or caches behind.");
            Require(File.Exists(cssInstall), "Turning Runtimes for tooling off removed a language server's installation.");
            Require(state.Current.PluginSettings[RuntimesPlugin.Id].GetProperty("bunPath").GetString() == "/pinned/bun", "The user's pinned path was not kept.");
            var off = await state.Languages.ResolveRuntimeAsync(RuntimeKind.Uv, directory, default);
            Require(!off.Available && off.OfferEnable, "With the plugin off the host answers with the offer to turn it on.");
            Console.WriteLine("PASS turning Runtimes for tooling off stops its processes and deletes every download, and keeps the pins");
        }
        finally
        {
            foreach (var process in new[] { downloadedUser, systemUser }) if (!process.HasExited) process.Kill(true);
            await state.StopLanguagesAsync();
        }

        await AcpDownloads(Path.Combine(root, "acp"));
    }

    private static IReadOnlyList<(Tereditor.Plugins.Api.PluginManifest Manifest, PluginHostModule? Host)> Plugins() =>
        [(RuntimesPlugin.Manifest, new RuntimesHost()), .. BuiltinPlugins.LanguageServerEntries];

    private static async Task AcpDownloads(string directory)
    {
        var data = Path.Combine(directory, "acp-data");
        string Touch(params string[] parts)
        {
            var file = Path.Combine([data, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
            return file;
        }
        var agent = Touch("agents", "goose", "goose");
        var registry = Touch("registry", "registry.json");
        var bunHome = Touch("bun", "install", "cache");
        var state = new HostStateStore(directory);
        await using (var quitting = new PluginRuntime(new PluginHostSeams { StateDirectory = directory, State = state, Builtins = [(AcpManifest.Manifest, new AcpHost { DataDirectory = data })] }))
        {
            await state.ChangeAsync([HostStateChange.PluginEnabled(AcpManifest.Id, true)]);
            await quitting.Start();
            Require((await quitting.ListAsync()).Single(entry => entry.Id == AcpManifest.Id).Status == Tereditor.Plugins.Api.PluginStatus.Active, "The ACP plugin did not start.");
        }
        Require(File.Exists(agent) && File.Exists(registry) && File.Exists(bunHome), "Quitting tereditor deleted what ACP downloaded.");
        await using var runtime = new PluginRuntime(new PluginHostSeams { StateDirectory = directory, State = state, Builtins = [(AcpManifest.Manifest, new AcpHost { DataDirectory = data })] });
        await runtime.Start();
        await state.ChangeAsync([HostStateChange.PluginEnabled(AcpManifest.Id, false)]);
        await runtime.Schedule();
        Require(!Directory.Exists(Path.Combine(data, "agents")) && !Directory.Exists(Path.Combine(data, "registry")) && !Directory.Exists(Path.Combine(data, "bun")), "Turning ACP off left installed agents or caches.");
        Console.WriteLine("PASS ACP deletes what it downloaded only when the user turns it off");
    }
}