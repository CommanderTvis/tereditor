using System.Diagnostics;
using System.Net;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using Tereditor.Checks.E2E;
using Tereditor.Host.Abstractions;
using Tereditor.Host.Client;
using Tereditor.Host.Protocol;
using Tereditor.Host.Remote;
using Tereditor.Plugins.Agent.Host;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Omp;
using Tereditor.Plugins.Omp.Host;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks;

// The omp plugin: the launch line (profile, session folder, model, approval mode, advisor, --no-lsp), the context-file
// resolution with nested, cycle-safe @-imports and the offers, the profile's MCP file, the version probe against a
// fixture omp and a missing one, process detection, revival, and the UI end to end.
internal static class OmpChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("omp: " + message);
    }

    private const string SignInSecret = "{\"anthropic\":{\"key\":\"fixture-signin-7f3a\"}}";

    private static string FakeOmp(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "omp");
        // auth export prints fixture credentials (or fails when $HOME/fail-export exists); the profile's import reads stdin into the fixture profile.
        File.WriteAllText(path, "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo \"omp 9.8.7\"; exit 0; fi\n" +
            "if [ \"$1\" = environment ]; then printf '%s\\n' \"$PI_CONFIG_FILES\" \"$(command -v omp)\"; exit 0; fi\n" +
            "if [ \"$1\" = auth ] && [ \"$2\" = export ] && [ -z \"$OMP_PROFILE\" ]; then if [ -f \"$HOME/fail-export\" ]; then echo \"error: rejected sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ012345\" >&2; exit 3; fi; " +
            "printf '%s\\n' '" + SignInSecret + "'; exit 0; fi\n" +
            "if [ \"$1\" = --profile ] && [ \"$3\" = auth ] && [ \"$4\" = import ] && [ \"$5\" = - ]; then mkdir -p \"$HOME/.omp/profiles/$2\" && cat > \"$HOME/.omp/profiles/$2/imported.json\"; exit $?; fi\n" +
            "if [ \"$1\" = --profile ] && [ \"$3\" = config ]; then d=\"$HOME/.omp/profiles/$2\"; mkdir -p \"$d\"; echo \"$4 $5 $6\" >> \"$HOME/config-calls.txt\"; " +
            "if [ -f \"$HOME/hang-config\" ]; then sleep 60; fi; if [ -f \"$HOME/fail-config\" ]; then echo broken >&2; exit 4; fi; " +
            "if [ \"$4\" = get ] && [ \"$5\" = startup.setupWizard ]; then cat \"$d/wizard\" 2>/dev/null || echo true; exit 0; fi; " +
            "if [ \"$4\" = get ] && [ \"$5\" = setupVersion ]; then cat \"$d/version\" 2>/dev/null || echo 0; exit 0; fi; " +
            "if [ \"$4\" = set ] && [ \"$5\" = startup.setupWizard ]; then echo \"$6\" > \"$d/wizard\"; exit 0; fi; exit 2; fi\n" +
            "printf '%s\\n' \"$@\" > \"" + Path.Combine(directory, "argv.txt") + "\"\nsleep 30\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static async Task RunHost(string root)
    {
        Directory.CreateDirectory(root);
        Launch();
        Context(Path.Combine(root, "omp-context"));
        Profile(Path.Combine(root, "omp-profile"));
        await SetupWizard(Path.Combine(root, "omp-wizard"));
        await Version(Path.Combine(root, "omp-version"));
        await Managed(Path.Combine(root, "omp-managed"));
        Processes();
        Sessions(Path.Combine(root, "omp-sessions"));
        Console.WriteLine("PASS omp host: launch line, context files and imports, profile MCP file, setup wizard switch, version and missing binary, managed omp on Bun, detection, sessions");
    }

    private static void Launch()
    {
        var plain = OmpLaunch.Line("", new() { Mcp = false });
        Require(plain == "omp --no-lsp", "a bare launch defaults to tereditor's code intelligence: " + plain);
        var full = OmpLaunch.Line("omp", new()
        {
            UiLaunch = true,
            Model = "anthropic/claude-opus-4-6:high",
            ApprovalMode = "write",
            Advisor = true,
            SessionId = "0123456789abcdef0123456789abcdef",
            SystemPrompt = "be brief",
            InitialPrompt = "fix it",
            Images = ["/tmp/a.png"]
        });
        Require(full == "TEREDITOR_UI_LAUNCH=omp omp --profile tereditor --session-dir \"$TEREDITOR_OMP_SESSIONS/0123456789abcdef0123456789abcdef\" " +
            "--model anthropic/claude-opus-4-6:high --approval-mode write --advisor --no-lsp --append-system-prompt 'be brief' @/tmp/a.png 'fix it'",
            "the launch carries the profile, session folder, model, approval mode, advisor and --no-lsp: " + full);
        Require(OmpLaunch.SessionIdOf(full) == "0123456789abcdef0123456789abcdef", "the session folder is read back from the arguments");
        var own = OmpLaunch.Line("omp", new() { Mcp = false, OwnCodeIntelligence = true });
        Require(own == "omp", "omp's own code intelligence drops --no-lsp: " + own);
        var resume = OmpLaunch.Line("omp", new() { Mcp = false, SessionDirectory = "/s/abc", ResumePath = "/s/abc/x.jsonl", InitialPrompt = "ignored" });
        Require(resume == "omp --session-dir /s/abc -r /s/abc/x.jsonl --no-lsp", "a resume names the session file and drops the opening prompt: " + resume);
        Require(OmpLaunch.Line("omp", new() { Windows = true, SessionId = "0123456789abcdef0123456789abcdef", UiLaunch = true }) == "omp --profile tereditor --no-lsp",
            "Windows launches keep omp's own session store and no POSIX marker");
        Require(OmpLaunch.ApprovalModes.SequenceEqual(["always-ask", "always-ask-cached", "write", "yolo"]) && OmpLaunch.ModelSuggestions.Contains("@smol"), "the documented values are suggested");
    }

    private static void Context(string root)
    {
        var home = Path.Combine(root, "home");
        var project = Path.Combine(root, "work", "project");
        Directory.CreateDirectory(Path.Combine(project, "docs"));
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        Directory.CreateDirectory(Path.Combine(home, ".omp", "profiles", "tereditor", "agent"));
        File.WriteAllText(Path.Combine(project, "AGENTS.md"), "# Project\n\n@docs/a.md\nmail me at someone@example.test\n```\n@docs/fenced.md\n```\nsee `@docs/inline.md`\n");
        File.WriteAllText(Path.Combine(project, "docs", "a.md"), "A\n@b.md\n");
        File.WriteAllText(Path.Combine(project, "docs", "b.md"), "B\n@a.md\n@missing.md\n");
        File.WriteAllText(Path.Combine(project, "docs", "fenced.md"), "never\n");
        File.WriteAllText(Path.Combine(project, "docs", "inline.md"), "never\n");
        File.WriteAllText(Path.Combine(project, ".claude", "CLAUDE.md"), "shadowed by AGENTS.md\n");
        File.WriteAllText(Path.Combine(root, "work", "AGENTS.md"), "ancestor\n");
        File.WriteAllText(Path.Combine(home, ".omp", "profiles", "tereditor", "agent", "AGENTS.md"), "user\n");

        var snapshot = OmpContext.Resolve(project, home, profile: true);
        string Rows() => string.Join(" | ", snapshot.Files.Select(file => $"{file.Label}:{file.Scope}:{file.Provider}:{file.Depth}{(file.Shadowed ? ":shadowed" : "")}{(file.Cycle ? ":cycle" : "")}"));
        var labels = snapshot.Files.Select(file => file.Label).ToList();
        Require(labels.IndexOf("~/.omp/profiles/tereditor/agent/AGENTS.md") == 0, "the profile's user file comes first: " + Rows());
        Require(labels.IndexOf(Path.Combine(root, "work", "AGENTS.md")) < labels.IndexOf("AGENTS.md"), "an ancestor comes before the launch directory: " + Rows());
        var agents = snapshot.Files.Single(file => file.Label == "AGENTS.md");
        Require(agents is { Provider: "native", Depth: 0, Shadowed: false, Scope: OmpContextScope.Project } && agents.Tokens == (agents.Bytes + 3) / 4, "the project AGENTS.md wins natively: " + Rows());
        Require(snapshot.Files.Single(file => file.Label == Path.Combine(".claude", "CLAUDE.md")).Shadowed, "the Claude file at the same depth is shadowed: " + Rows());
        var a = labels.IndexOf(Path.Combine("docs", "a.md"));
        Require(a == labels.IndexOf("AGENTS.md") + 1 && snapshot.Files[a].Depth == 1, "an import follows its importer at depth one: " + Rows());
        Require(snapshot.Files[a + 1] is { Depth: 2, Cycle: false } && snapshot.Files[a + 1].Label == Path.Combine("docs", "b.md"), "a nested import is depth two: " + Rows());
        Require(snapshot.Files[a + 2] is { Depth: 3, Cycle: true } && snapshot.Files[a + 2].Label == Path.Combine("docs", "a.md"), "a cycle is marked and not followed: " + Rows());
        Require(!labels.Any(label => label.Contains("fenced", StringComparison.Ordinal) || label.Contains("inline", StringComparison.Ordinal) || label.Contains("missing", StringComparison.Ordinal) || label.Contains("example", StringComparison.Ordinal)),
            "code, e-mail addresses and unreadable imports are not imports: " + Rows());
        // A chain longer than five hops stops at the fifth.
        for (var hop = 0; hop < 8; hop++) File.WriteAllText(Path.Combine(project, "docs", $"chain{hop}.md"), $"@chain{hop + 1}.md\n");
        File.WriteAllText(Path.Combine(project, "docs", "chain8.md"), "end\n");
        Directory.CreateDirectory(Path.Combine(project, ".omp"));
        File.WriteAllText(Path.Combine(project, ".omp", "RULES.md"), "@../docs/chain0.md\n");
        snapshot = OmpContext.Resolve(project, home, profile: true);
        Require(snapshot.Files.Where(file => file.Label.Contains("chain", StringComparison.Ordinal)).Max(file => file.Depth) == OmpContext.MaxImportDepth, "imports stop after five hops: " + Rows());

        Require(snapshot.Offers.All(offer => offer.Target != OmpContextTarget.Agents) && snapshot.Offers.All(offer => offer.Target != OmpContextTarget.Rules) &&
            snapshot.Offers.Any(offer => offer is { Target: OmpContextTarget.AppendSystem, Label: "Add .omp/APPEND_SYSTEM.md" }), "only missing files are offered");
        var created = OmpContext.Create(project, OmpContextTarget.AppendSystem);
        Require(File.Exists(created) && OmpContext.Resolve(project, home, true).Offers.Count == 0, "adding a file fulfils its offer");
        File.WriteAllText(created, "mine\n");
        OmpContext.Create(project, OmpContextTarget.AppendSystem);
        Require(File.ReadAllText(created) == "mine\n", "adding never overwrites an existing file");
        Require(OmpContext.Resolve(project, home, profile: false).UserDirectory == Path.Combine(home, ".omp", "agent"), "without the profile, user files come from omp's own directory");
    }

    private static void Profile(string root)
    {
        OmpHost.WriteProfileMcp(root);
        var path = OmpHost.ProfileMcpPath(root);
        Require(path == Path.Combine(root, ".omp", "profiles", "tereditor", "agent", "mcp.json"), "the profile's MCP file path: " + path);
        var server = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("mcpServers").GetProperty("tereditor");
        Require(server.GetProperty("type").GetString() == "http" && server.GetProperty("url").GetString() == "${THINKRAIL_MCP_URL}",
            "the server's URL is each terminal's own, expanded by omp");
        File.WriteAllText(path, "{}");
        OmpHost.WriteProfileMcp(root);
        Require(File.ReadAllText(path).Contains("THINKRAIL_MCP_URL", StringComparison.Ordinal) && Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1,
            "a rewrite replaces the file whole and leaves no temporary file");
    }

    // The profile's first-run setup wizard is switched off once, only while it never completed, and never blocks on failures.
    private static async Task SetupWizard(string root)
    {
        var fake = FakeOmp(root);
        string Home(string name) { var h = Path.Combine(root, name); Directory.CreateDirectory(h); return h; }
        string Calls(string h) => File.Exists(Path.Combine(h, "config-calls.txt")) ? File.ReadAllText(Path.Combine(h, "config-calls.txt")) : "";
        string Wizard(string h) => File.ReadAllText(Path.Combine(h, ".omp", "profiles", "tereditor", "wizard")).Trim();

        var fresh = Home("fresh");
        Require(await OmpHost.SkipSetupWizardAsync(fake, fresh) == "set" && Wizard(fresh) == "false", "a never-completed wizard is turned off");
        var calls = Calls(fresh);
        Require(await OmpHost.SkipSetupWizardAsync(fake, fresh) != "set" && !Calls(fresh)[calls.Length..].Contains("set startup", StringComparison.Ordinal), "a second preparation does not set it again");

        var done = Home("done");
        Directory.CreateDirectory(Path.Combine(done, ".omp", "profiles", "tereditor"));
        File.WriteAllText(Path.Combine(done, ".omp", "profiles", "tereditor", "version"), "3\n");
        Require(await OmpHost.SkipSetupWizardAsync(fake, done) != "set" && !Calls(done).Contains("set startup", StringComparison.Ordinal), "a completed wizard (setupVersion > 0) is left alone");

        var failing = Home("failing");
        File.WriteAllText(Path.Combine(failing, "fail-config"), "");
        Require((await OmpHost.SkipSetupWizardAsync(fake, failing)).StartsWith("unchanged", StringComparison.Ordinal), "a failing config get changes nothing and does not throw");
        var hanging = Home("hanging");
        File.WriteAllText(Path.Combine(hanging, "hang-config"), "");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Require((await OmpHost.SkipSetupWizardAsync(fake, hanging)).StartsWith("failed", StringComparison.Ordinal) && watch.Elapsed < TimeSpan.FromSeconds(25), "a hanging omp is cut off by the timeout");
        Require((await OmpHost.SkipSetupWizardAsync(Path.Combine(root, "nowhere", "omp"), fresh)).StartsWith("failed", StringComparison.Ordinal), "a missing omp is tolerated");
    }

    private static async Task Version(string root)
    {
        var fake = FakeOmp(root);
        var status = await OmpHost.ReadStatusAsync(fake, root, true);
        Require(status is { Installed: true, Version: "9.8.7" } && status.ProfileMcpPath == OmpHost.ProfileMcpPath(root), "the version comes from omp --version: " + status);
        var missing = await OmpHost.ReadStatusAsync(Path.Combine(root, "nowhere", "omp"), root, false);
        Require(missing is { Installed: false, Version: null } && missing.Detail!.Contains("not found", StringComparison.Ordinal), "a missing binary is reported: " + missing);
    }


    private static Task<(int Code, string Output, string Errors)> RunProgram(string program, params string[] arguments) => RunWithOverlays("", program, arguments);

    // Runs the program with the PI_CONFIG_FILES a user's shell would hand it; empty means none.
    private static async Task<(int Code, string Output, string Errors)> RunWithOverlays(string configFiles, string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (configFiles.Length > 0) start.Environment["PI_CONFIG_FILES"] = configFiles;
        else start.Environment.Remove("PI_CONFIG_FILES");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("not started");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, await output, await errors);
    }

    // The pinned omp: downloaded once with Bun and checked against the pinned integrity, then run as `bun --bun cli.js` through the program the launch line
    // starts, never the system omp. A fake Bun and a fake package stand in; nothing here touches the network.
    private static async Task Managed(string root)
    {
        // The folder has a space: the launch word has to be quoted and still be recognized as omp.
        var directory = Path.Combine(root, "with space");
        Directory.CreateDirectory(directory);
        FakeOmp(directory);
        var bun = ManagedAgentChecks.FakeBun(directory, OmpManaged.Package, OmpManaged.Version, OmpManaged.Integrity, "dist/cli.js", "omp");
        var calls = () => ManagedAgentChecks.Calls(directory);
        var resolution = ManagedAgentChecks.Resolution(directory);
        var install = Path.Combine(directory, "state", "omp", "managed");
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);
        var managed = new OmpManaged(install, (_, _) => Task.FromResult(resolution));
        var unavailable = RuntimeResolution.Unavailable(RuntimeKind.Bun, "Needs Bun.", offerEnable: true);

        // The package is the real one, pinned exactly.
        Require(OmpManaged.Package == "@oh-my-pi/pi-coding-agent" && OmpManaged.Version == "19.2.1" && OmpManaged.Integrity.StartsWith("sha512-", StringComparison.Ordinal), "omp is pinned to one exact version of its package.");
        Require(managed.EntryPoint == Path.Combine(install, "19.2.1", "node_modules", "@oh-my-pi", "pi-coding-agent", "dist", "cli.js"), "The script Bun runs is the package's omp binary, dist/cli.js.");

        // The default: managed while Bun is there or can be downloaded, the system command only when it is not; a choice stands.
        Require(new OmpSettings().Source == "" && new OmpSettings().Command == "omp", "A fresh setup has no source chosen and the system command omp.");
        Require(OmpSources.Effective("", true) == OmpSources.Managed && OmpSources.Effective("", false) == OmpSources.System, "Without a choice omp is managed while Bun is available and system without it.");
        Require(OmpSources.Effective(OmpSources.Managed, false) == OmpSources.Managed && OmpSources.Effective(OmpSources.System, true) == OmpSources.System, "A choice of the user stands whether or not Bun is available.");
        var missingCommand = Path.Combine(root, "nowhere", "omp");
        var before = await OmpHost.ComposeStatusAsync(new OmpSettings { Command = missingCommand }, root, managed, bunAvailable: true);
        Require(before is { Source: OmpSources.Managed, Installed: false, Usable: true, PinnedVersion: OmpManaged.Version, ManagedVersion: "", Installing: false } && before.LaunchCommand == managed.ShimPath && before.Detail!.Contains("not downloaded", StringComparison.Ordinal),
            "Before a download the managed omp is pinned, not installed and usable, so the chip is not blocked on a download: " + before);
        var withoutBun = await OmpHost.ComposeStatusAsync(new OmpSettings { Command = missingCommand }, root, managed, bunAvailable: false);
        Require(withoutBun is { Source: OmpSources.System, Usable: false, LaunchCommand: "" } && withoutBun.SourceNote.Contains("Bun", StringComparison.Ordinal), "Without Bun the default falls back to the system command and says so: " + withoutBun);
        var pinnedNoBun = await OmpHost.ComposeStatusAsync(new OmpSettings { Source = OmpSources.Managed }, root, managed, bunAvailable: false);
        Require(pinnedNoBun is { Source: OmpSources.Managed, Installed: false, Usable: false } && pinnedNoBun.Detail!.Contains("Bun is not available", StringComparison.Ordinal), "A chosen managed omp without Bun is not usable and names Bun: " + pinnedNoBun);
        var chosenSystem = await OmpHost.ComposeStatusAsync(new OmpSettings { Source = OmpSources.System, Command = FakeOmp(Path.Combine(root, "system")) }, root, managed, bunAvailable: true);
        Require(chosenSystem is { Source: OmpSources.System, Installed: true, Usable: true, Version: "9.8.7", SourceNote: "" } && chosenSystem.LaunchCommand.Length == 0, "A chosen system command runs its own --version and never the managed copy: " + chosenSystem);

        // The program a launch starts, before anything is downloaded: it says what is missing and exits 1.
        var noBunShim = managed.WriteShim(null);
        if (!OperatingSystem.IsWindows())
        {
            var noBun = await RunProgram(noBunShim, "--version");
            Require(noBun.Code == 1 && noBun.Errors.Contains("needs Bun", StringComparison.Ordinal), "With no Bun the program says so and exits 1: " + noBun.Errors);
            managed.WriteShim(resolution);
            var notYet = await RunProgram(managed.ShimPath, "--version");
            Require(notYet.Code == 1 && notYet.Errors.Contains("not downloaded yet", StringComparison.Ordinal) && calls().Length == 0, "Before the download the program says so, exits 1 and starts no Bun: " + notYet.Errors);
        }

        // A copy that the earlier pin downloaded stays as it is: reading the status, writing the program and starting it run no Bun. Only Update downloads.
        var earlier = Path.Combine(install, "19.1.1");
        Directory.CreateDirectory(earlier);
        var kept = await OmpHost.ComposeStatusAsync(new OmpSettings(), root, managed, bunAvailable: true);
        Require(kept is { Installed: false, ManagedVersion: "19.1.1", PinnedVersion: OmpManaged.Version, Installing: false, Error: "" } && !managed.Running,
            "With only an earlier pin's copy the page offers Update and starts no download: " + kept);
        if (!OperatingSystem.IsWindows())
        {
            var stale = await RunProgram(managed.WriteShim(resolution), "--version");
            Require(stale.Code == 1 && stale.Errors.Contains("not downloaded yet", StringComparison.Ordinal) && calls().Length == 0 && Directory.GetFileSystemEntries(install).SequenceEqual([earlier]),
                "A launch with only an earlier pin's copy downloads nothing: " + stale.Errors);
        }
        Directory.Delete(earlier);

        await ManagedAgentChecks.Download(managed, directory, OmpManaged.Package, OmpManaged.Version, workspace,
            () => managed.Status(before) is var status ? (status.Installing, status.Progress, status.Error) : default);
        var after = await OmpHost.ComposeStatusAsync(new OmpSettings { Command = missingCommand }, root, managed, bunAvailable: true);
        Require(after is { Source: OmpSources.Managed, Installed: true, Usable: true, Version: OmpManaged.Version, ManagedVersion: OmpManaged.Version, Error: "" }, "The account's version is the pinned one on disk: " + after);

        // The launch: the program runs bun --bun cli.js with the arguments of the line; a Model source's contribution still wraps it.
        Require(File.Exists(managed.ShimPath) && File.ReadAllText(managed.ShimPath).Contains(managed.EntryPoint, StringComparison.Ordinal), "A finished download writes the program that runs it.");
        var word = OmpLaunch.ManagedExecutable(managed.ShimPath);
        var governed = OmpLaunch.Line(word, new()
        {
            Mcp = false,
            Contributions = [new AgentLaunchContribution { Environment = new Dictionary<string, string> { ["MODEL_SOURCE"] = "mine" }, Arguments = ["--profile", "tereditor"], Wrapper = ["wrap"] }]
        });
        Require(word.StartsWith('\'') && governed == "MODEL_SOURCE=mine wrap " + word + " --profile tereditor --no-lsp", "A launch of the managed omp keeps its executable word, so a Model source's contribution still applies: " + governed);
        Require(AgentLaunchComposition.Apply("\"C:/Users/x/.tereditor/omp/shim/bin/omp.cmd\" --no-lsp", "omp", [new AgentLaunchContribution { Environment = new Dictionary<string, string> { ["K"] = "v" } }], windows: true).StartsWith("\"C:/Users", StringComparison.Ordinal) &&
            AgentLaunchComposition.Apply("\"C:/Users/x/omp.cmd\"", "omp", [new AgentLaunchContribution { Wrapper = ["wrap"] }], windows: true).StartsWith("wrap ", StringComparison.Ordinal), "The Windows program omp.cmd is recognized as omp.");
        if (!OperatingSystem.IsWindows())
        {
            var run = await RunProgram(managed.ShimPath, "--version");
            Require(run.Code == 0 && run.Output.Contains("omp 9.8.7", StringComparison.Ordinal) && calls().Single() == $"--bun {managed.EntryPoint} --version", "The program runs Bun with the script first and omp's own arguments after it: " + string.Join(" | ", calls()));
            Require(File.GetUnixFileMode(managed.ShimPath).HasFlag(UnixFileMode.UserExecute) && File.ReadAllText(managed.ShimPath).Contains("export BUN_INSTALL=", StringComparison.Ordinal), "The program is executable and carries Bun's environment.");

            // The copy cannot update itself: its runs add tereditor's settings after any of the user's, and the omp it finds on PATH is this program,
            // two folders below the file that tells `omp update` who keeps it.
            var settings = Path.Combine(managed.ShimDirectory, "settings.yml");
            var probe = await RunProgram(managed.ShimPath, "environment");
            Require(probe.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).SequenceEqual([settings, managed.ShimPath]), "The run names tereditor's settings and finds this program as omp: " + probe.Output + probe.Errors);
            var layered = await RunWithOverlays("/user/overlay.yml", managed.ShimPath, "environment");
            Require(layered.Output.Split('\n')[0] == "/user/overlay.yml:" + settings, "tereditor's settings come after the user's, so they win: " + layered.Output);
            Require(File.ReadAllText(settings).Contains("startup:\n  checkUpdate: false\nupdate:\n  auto: false\n", StringComparison.Ordinal), "The settings turn off the update check, its notice and the background update.");
            var manager = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(managed.ShimPath))!, "manager.json");
            using (var kind = JsonDocument.Parse(File.ReadAllText(manager)))
                Require(kind.RootElement.GetProperty("name").GetString() == "tereditor" && kind.RootElement.GetProperty("manager").GetString() == "tereditor", "omp update reads that tereditor keeps this install.");
            File.Delete(manager);
            var count = calls().Length;
            var unprotected = await RunProgram(managed.ShimPath, "--version");
            Require(unprotected.Code == 1 && calls().Length == count, "Without those files the program does not start omp.");
            managed.WriteShim(resolution);

            // The setup wizard switch and the sign-in import start the same program.
            var home = Path.Combine(directory, "home");
            Directory.CreateDirectory(home);
            Require(await OmpHost.SkipSetupWizardAsync(managed.ShimPath, home) == "set" && File.ReadAllText(Path.Combine(home, ".omp", "profiles", "tereditor", "wizard")).Trim() == "false", "The setup wizard switch runs through the managed program.");
            var signIn = await OmpHost.ImportSignInAsync(managed.ShimPath, home, CancellationToken.None);
            Require(signIn.Success && File.ReadAllText(Path.Combine(home, ".omp", "profiles", "tereditor", "imported.json")).Trim() == SignInSecret, "The sign-in import runs through the managed program.");

            // A Bun that moved is followed: the program is rewritten only when its text changes.
            var written = File.GetLastWriteTimeUtc(managed.ShimPath);
            managed.WriteShim(resolution);
            Require(File.GetLastWriteTimeUtc(managed.ShimPath) == written, "An unchanged program is not rewritten.");
            managed.WriteShim(resolution with { Command = Path.Combine(directory, "bun2") });
            Require(File.ReadAllText(managed.ShimPath).Contains("bun2", StringComparison.Ordinal), "A program that no longer matches Bun is rewritten.");
        }

        managed.Remove();
        Require(!Directory.Exists(install) && !Directory.Exists(managed.ShimDirectory) && managed.InstalledVersion().Length == 0, "Remove deletes the downloaded omp and its program.");
    }

    private static void Processes()
    {
        var rows = ProcessSnapshot.ParseRows(ProcessSnapshot.Living("  10     1 Ss   zsh\n  11    10 Z    omp\n  12    10 S+   /usr/local/bin/omp\n"));
        var snapshot = new ProcessSnapshot(rows);
        Require(snapshot.FindDescendant(10, ["omp"]) is { Pid: 12 }, "a live omp under the shell is found and a zombie one skipped");
        var zombie = new ProcessSnapshot(ProcessSnapshot.ParseRows(ProcessSnapshot.Living("  10     1 Ss   zsh\n  11    10 Z    omp\n")));
        Require(zombie.FindDescendant(10, ["omp"]) is null, "an exited, unreaped omp keeps no agent");
        var nested = new ProcessSnapshot([new(10, 1, "zsh"), new(20, 10, "claude"), new(30, 20, "Tereditor.UI"), new(40, 30, "zsh"), new(50, 40, "omp")]);
        Require(nested.FindDescendant(10, ["omp"]) is null && nested.FindDescendant(30, ["omp"]) is { Pid: 50 }, "omp below an inner tereditor app is not the outer tab's agent");
        // The managed omp is a Bun process; only the one that runs the managed copy counts, and another Bun beside it does not hide it.
        var bunRows = new ProcessSnapshot([new(10, 1, "zsh"), new(20, 10, "bun"), new(30, 10, "bun")]);
        Require(bunRows.FindDescendant(10, ["omp", "bun"], accepts: (pid, _) => pid == 30) is { Pid: 30, Name: "bun" } && bunRows.FindDescendant(10, ["omp", "bun"], accepts: (_, _) => false) is null && bunRows.FindDescendant(10, ["omp"]) is null,
            "A Bun process counts as omp only when it is the managed copy's");
        var found = new List<int>();
        using var watch = new AgentWatch("omp", () => [new("w", "t", 10)], (_, _, pid) => found.Add(pid), (_, _) => { }, () => bunRows, pid => pid == 30);
        watch.Sweep();
        Require(found.SequenceEqual([30]) && watch.AgentFor("w", "t") == "bun", "The watch records a tab running the managed omp");
    }

    private static void Sessions(string root)
    {
        var folder = Path.Combine(root, "abc");
        Require(OmpHost.NewestSession(folder) is null, "a session folder that was never written has no session");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "old.jsonl"), "{}\n");
        File.SetLastWriteTimeUtc(Path.Combine(folder, "old.jsonl"), DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(Path.Combine(folder, "nested", "new.jsonl"), "{}\n");
        Require(OmpHost.NewestSession(folder) == Path.Combine(folder, "nested", "new.jsonl"), "the newest session in the folder is resumed");
    }

    // The UI half against the real host over the remote adapter: the Early badges, version, missing state, context rows and offer, and a launch.
    internal static void RunUi(string root)
    {
        AgentUiChecks.OmpSettings();
        Directory.CreateDirectory(root);
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var previousHome = Environment.GetEnvironmentVariable(OmpHost.HomeVariable);
        Environment.SetEnvironmentVariable(OmpHost.HomeVariable, home);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), "# Rules\n@docs/more.md\n");
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        File.WriteAllText(Path.Combine(workspace, "docs", "more.md"), "@../AGENTS.md\n");
        var fake = FakeOmp(Path.Combine(root, "fixture"));
        const string token = "omp-fixture";
        var server = RemoteServer.Create(workspace, IPAddress.Loopback, 0, token, workspace + "-host");
        Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
        var endpoint = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var terminals = new RemoteTerminalAdapter(endpoint, token);
        var app = new E2eWorkspace(endpoint, token, workspace, workspace + "-profile", workspace, terminals: new E2eTerminals(terminals));
        try
        {
            E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
            void Settings(object settings)
            {
                var json = JsonSerializer.Serialize(settings);
                _ = app.Workbench.State.ChangeAsync(HostStateChange.PluginSettings(OmpManifest.Id, json));
                E2eWorkspace.Until(() => app.Workbench.State.Current.PluginSettings.GetValueOrDefault(OmpManifest.Id) is { ValueKind: JsonValueKind.Object } stored &&
                    JsonSerializer.SerializeToElement(settings).EnumerateObject().All(member => stored.TryGetProperty(member.Name, out var value) && value.GetRawText() == member.Value.GetRawText()));
            }
            Settings(new { command = fake, source = "system", model = "@smol", approvalMode = "write", advisor = true });
            _ = app.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(OmpManifest.Id, true));
            E2eWorkspace.Until(() => app.Workbench.PluginLoader.Registry.Active.Contains(OmpManifest.Id));
            Require(app.Workbench.PluginLoader.Registry.Roster.Single(entry => entry.Id == OmpManifest.Id).Support == Tereditor.Plugins.Api.PluginSupportLevels.Early,
                "the roster carries the Early support level over the wire");

            AgentPaneChecks.Show(app, "omp");
            E2eWorkspace.Until(() => Find<Control>(app.Window, "OmpPanel").Any());
            var panel = Find<Control>(app.Window, "OmpPanel").Single();
            AgentPaneChecks.Tab(app, "account");
            E2eWorkspace.Until(() => Find<TextBlock>(panel, "OmpVersion").Single().Text == "v9.8.7");
            AgentPaneChecks.Tab(app, "settings");
            var badge = Find<Border>(panel, "OmpEarlyBadge").Single();
            var reload = Find<Button>(panel, "OmpRefresh").Single();
            Require(badge.IsEffectivelyVisible, "the pane wears the Early badge");
            Require(badge.Bounds.Height is > 0 and < 20 && badge.TranslatePoint(default, panel)!.Value.Y < reload.TranslatePoint(default, panel)!.Value.Y + reload.Bounds.Height,
                $"the badge is a small tag on the reload button's row, not stretched to it: {badge.Bounds}");
            AgentPaneChecks.Tab(app, "account");
            Require(Find<Control>(panel, "OmpProvider").Single() is var provider && provider.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "omp's own provider (built in)"),
                "the Account tab names the model provider omp runs under");
            AgentPaneChecks.Tab(app, "settings");
            Require(Find<TextBlock>(panel, "OmpCodeIntelligence").Single().Text!.Contains("tereditor", StringComparison.Ordinal), "the pane says sessions use tereditor's code intelligence");
            Require(Find<TextBlock>(panel, "OmpProfileNote").Single().IsEffectivelyVisible, "the pane notes what the profile keeps apart");
            AgentPaneChecks.Tab(app, "context");
            E2eWorkspace.Until(() => Find<Control>(panel, "OmpContextRow").Count >= 3);
            var rows = Find<Control>(panel, "OmpContextRow");
            Require(rows.Any(row => row.Margin.Left == 16 && Equals(row.Tag, Path.Combine(workspace, "docs", "more.md"))) &&
                rows.Any(row => row.Margin.Left == 32 && Find<TextBlock>(row, "OmpContextTokens").Single().Text == "cycle"), "imports are nested rows and the cycle is marked");
            Require(rows.First(row => Equals(row.Tag, Path.Combine(workspace, "AGENTS.md"))) is var agents && Find<TextBlock>(agents, "OmpContextTokens").Single().Text!.EndsWith("tok", StringComparison.Ordinal),
                "a file shows its token estimate");
            // In the real pane only the path's text is the link. The fixture's temporary path fills the row, so a short label stands in.
            var source = Find<Button>(agents, "OmpContextSource").Single();
            var sourceText = (TextBlock)source.Content!;
            var fullText = sourceText.Text;
            var clicks = 0;
            sourceText.Text = "AGENTS.md";
            source.Click += (_, _) => clicks++;
            LinkHitArea.Check(app.Window, source, () => clicks, "an omp context row");
            sourceText.Text = fullText;
            E2eWorkspace.Until(() => Find<Button>(panel, "OmpOffer_rules").Any());
            Require(!Find<Button>(panel, "OmpOffer_agents").Any(), "an existing AGENTS.md is not offered");
            app.Click(Find<Button>(panel, "OmpOffer_rules").Single());
            E2eWorkspace.Until(() => File.Exists(Path.Combine(workspace, ".omp", "RULES.md")) && !Find<Button>(panel, "OmpOffer_rules").Any());
            Require(File.Exists(OmpHost.ProfileMcpPath(home)), "enabling writes the profile's MCP file under omp's home");

            // The sign-in import pipes the fixture's export into the profile's import on the host, only when pressed.
            var imported = Path.Combine(home, ".omp", "profiles", "tereditor", "imported.json");
            Require(!File.Exists(imported), "the sign-in import never runs by itself");
            AgentPaneChecks.Tab(app, "account");
            E2eWorkspace.Until(() => Find<StackPanel>(panel, "OmpSignIn").Single().IsEffectivelyVisible);
            var signIn = Find<Button>(panel, "OmpImportSignIn").Single();
            var signInResult = Find<TextBlock>(panel, "OmpSignInResult").Single();
            app.Click(signIn);
            E2eWorkspace.Until(() => signInResult.Text?.Contains("now works", StringComparison.Ordinal) == true && signIn.IsEnabled);
            Require(File.ReadAllText(imported).Trim() == SignInSecret, "the default profile's export lands in the tereditor profile");
            var leaks = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => file != imported && !file.StartsWith(Path.Combine(root, "fixture"), StringComparison.Ordinal) && new FileInfo(file).Length < 4 << 20)
                .Where(file => { try { return File.ReadAllText(file).Contains("fixture-signin-7f3a", StringComparison.Ordinal); } catch (IOException) { return false; } }).ToList();
            Require(leaks.Count == 0, "tereditor persists no credentials: " + string.Join(", ", leaks));
            File.WriteAllText(Path.Combine(home, "fail-export"), "");
            app.Click(signIn);
            E2eWorkspace.Until(() => signInResult.Text?.Contains("exit 3", StringComparison.Ordinal) == true && signIn.IsEnabled);
            Require(!signInResult.Text!.Contains("sk-ABCDEFGHIJ", StringComparison.Ordinal) && signInResult.Text.Contains("***", StringComparison.Ordinal),
                "a failure is reported with token-like text masked: " + signInResult.Text);
            Settings(new { command = fake, source = "system", model = "@smol", approvalMode = "write", advisor = true, mcp = false });
            E2eWorkspace.Until(() => !Find<StackPanel>(panel, "OmpSignIn").Single().IsVisible);
            Settings(new { command = fake, source = "system", model = "@smol", approvalMode = "write", advisor = true, mcp = true });
            E2eWorkspace.Until(() => Find<StackPanel>(panel, "OmpSignIn").Single().IsVisible);

            // The start action launches the fixture with the configured flags in its own session folder.
            E2eWorkspace.Until(() => Find<Button>(app.Window, "NewOmp").Any());
            // The start action wears omp's own mark (the asset drawn), not the terminal glyph that stands in until it is read.
            E2eWorkspace.Until(() => Find<Button>(app.Window, "NewOmp").First().Content is ContentControl { Tag: OmpManifest.Icon, Content: SvgAsset });
            // The launcher's own icon factory (new-agent menu, Start work, phone strip) draws the mark in whichever theme brush it is given.
            var launcherIcon = app.Workbench.PluginLoader!.Registry.LauncherList.Single(launcher => launcher.Id == OmpManifest.Id).CreateIcon!;
            foreach (var brush in new[] { Ui.Accent, Ui.TextBrush })
            {
                var icon = (ContentControl)launcherIcon(14, brush);
                E2eWorkspace.Until(() => icon.Content is SvgAsset { Width: 14 });
                Require(Equals(icon.Tag, OmpManifest.Icon), "the launcher icon carries omp's mark, not the terminal glyph");
            }
            var argv = Path.Combine(root, "fixture", "argv.txt");
            app.Click(Find<Button>(app.Window, "NewOmp").First());
            E2eWorkspace.Until(() => File.Exists(argv) && File.ReadAllText(argv).Contains("--no-lsp", StringComparison.Ordinal));
            var args = File.ReadAllLines(argv);
            Require(args.Take(2).SequenceEqual(["--profile", "tereditor"]) && args[2] == "--session-dir" &&
                OmpLaunch.SessionIdOf("--session-dir " + args[3]) is not null && args.Skip(4).SequenceEqual(["--model", "@smol", "--approval-mode", "write", "--advisor", "--no-lsp"]),
                "the launch argv: " + string.Join(" ", args));
            // The Agent pane's chip wears the same mark.
            E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Any(chip => chip.Name == "AgentChip_omp" &&
                chip.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, OmpManifest.Icon))));

            // Every omp mark on screen (the start action and the chip among them) is drawn for the omp agent, so Colored agent icons reach all of them.
            E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Where(control => Equals(control.Tag, OmpManifest.Icon)).ToArray() is { Length: >= 2 } marks &&
                marks.All(mark => mark.GetLogicalDescendants().OfType<SvgAsset>().Any(asset => asset.Agent == "omp")));

            // A missing binary: the pane says so with the install link, and the launcher is unavailable.
            Settings(new { command = Path.Combine(root, "nowhere", "omp"), source = "system", model = "@smol", approvalMode = "write", advisor = true });
            // The settings change re-reads the state, so the pane's chip goes away and the launcher says why it cannot start; the pane's own page still shows the missing state.
            E2eWorkspace.Until(() => !app.Window.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Any(chip => chip.Name == "AgentChip_omp"));
            E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.LauncherList.Single(launcher => launcher.Id == OmpManifest.Id).Availability() is { Available: false, Reason: { } reason } && reason.Contains("not installed", StringComparison.Ordinal));
            Find<Button>(panel, "OmpRefresh").Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2eWorkspace.Until(() => Find<StackPanel>(panel, "OmpMissing").Single().IsVisible);
            Require(Find<Button>(panel, "OmpInstall").Single().IsEffectivelyVisible && Find<TextBlock>(panel, "OmpMissingText").Single().Text!.Contains("not installed", StringComparison.Ordinal),
                "the missing state offers the install link");
            // A chosen managed omp starts through the host's program under omp's home, never the configured command, and the page asks for a download first.
            Settings(new { command = fake, source = "managed", model = "@smol", approvalMode = "write", advisor = true });
            var managedProgram = Path.Combine(home, ".tereditor", "omp", "shim", "bin", "omp");
            E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.LauncherList.Single(launcher => launcher.Id == OmpManifest.Id).TerminalCommand(new()) is { } managedLine &&
                managedLine.Contains(managedProgram, StringComparison.Ordinal) && !managedLine.Contains(fake, StringComparison.Ordinal));
            Require(File.Exists(managedProgram), "the host wrote the program the managed launch starts, under omp's home");
            Console.WriteLine("PASS omp UI: Early badge, version, code intelligence and profile notes, nested context rows, add offer, sign-in import, launch argv, omp mark on the start action and tab, missing binary, managed launch program");
        }
        finally
        {
            app.Dispose();
            terminals.Dispose();
            Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable(OmpHost.HomeVariable, previousHome);
        }
    }

    private static List<T> Find<T>(Control scope, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs();
        (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return [.. new[] { scope }.Concat(scope.GetLogicalDescendants()).OfType<T>().Where(item => item.Name == name)];
    }
}