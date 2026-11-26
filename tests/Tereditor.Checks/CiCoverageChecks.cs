using System.Text.RegularExpressions;

namespace Tereditor.Checks;

/// <summary>
/// Every check mode must say how it reaches CI. A mode is named by a literal on an `if (args...)` line of Program.cs or of the
/// runner's Main; each one needs an entry in <see cref="Modes"/>: the gate cases that run it, the CI job that runs it, or an
/// exemption with its reason. A new mode without an entry, an entry whose mode is gone, a case that is not registered and a job
/// that does not mention the mode all fail. This pins a decision per mode; it does not prove that a case covers the whole body of its mode.
/// </summary>
internal static partial class CiCoverageChecks
{
    internal abstract record Cover;
    internal sealed record Gated(string[] Cases) : Cover;
    internal sealed record Job(string Workflow, string Needle) : Cover;
    internal sealed record Matrix(string Mode) : Cover;
    internal sealed record Exempt(string Reason) : Cover;

    private static Gated Cases(params string[] cases) => new(cases);

    private static readonly Dictionary<string, Cover> Modes = new()
    {
        ["--about"] = Cases("AboutChecks"),
        ["--acp"] = Cases("acp"),
        ["--agent-launches"] = Cases("agent-launch-origins", "agent-launch-contributions", "AgentLaunchNotices"),
        ["--agent-marks"] = Cases("AgentTabMarksE2E", "RemoteAgentMarksE2E", "TerminalSessionIdE2E"),
        ["--agent-pane"] = Cases("agent-pane"),
        ["--agent-session-ownership"] = Cases("agent-session-ownership"),
        ["--air-gateway"] = Cases("air-gateway", "air-gateway-ui"),
        ["--android"] = Cases("android-client"),
        ["--android-package"] = new Exempt("Not in the argument-free suite (docs/develop/agent-reference.md): needs the built APK from scripts/mise run package:android; nightly.yml builds it but runs no check on it yet"),
        ["--blueprint"] = Cases("blueprint", "blueprint-ui"),
        ["--bottom-panel"] = Cases("BottomPanelE2E"),
        ["--branch-graph"] = Cases("BranchGraphChecks-RunHostAsync", "BranchGraphChecks-RunUi"),
        ["--branch-status"] = Cases("branch-status"),
        ["--branches"] = Cases("BranchListE2E"),
        ["--browser"] = Cases("browser-transport", "websocket-flow"),
        ["--button-centering"] = Cases("button-centering"),
        ["--host-launch"] = Cases("host-launch"),
        ["--change-actions"] = Cases("ChangeActionChecks"),
        ["--changes"] = Cases("changes"),
        ["--changes-branch-switch"] = Cases("ChangesBranchSwitchChecks"),
        ["--changes-menu"] = Cases("ChangesE2E", "ChangesScopeE2E"),
        ["--check-root"] = Cases("check-root"),
        ["--check-root-child"] = new Exempt("Fixture, not a check: the child process of the check-root case"),
        ["--claude-chat-e2e"] = Cases("claude-chat-e2e"),
        ["--claude-chat-host"] = Cases("claude-chat-host"),
        ["--claude-chat-ui"] = Cases("claude-chat-ui", "claude-chat-ui-menus", "claude-chat-ui-transcript", "claude-chat-ui-composer"),
        ["--claude-code"] = Cases("claude-code", "claude-code-ui", "claude-code-launcher"),
        ["--closed-chat-views"] = Cases("claude-chat-ui", "closed-chat-tabs"),
        ["--cloud-platforms"] = Cases("cloud-platforms"),
        ["--code-intelligence-plugin"] = Cases("code-intelligence-plugin"),
        ["--codex"] = Cases("codex-host", "codex-ui"),
        ["--codex-terminals"] = Cases("codex-terminals"),
        ["--commands"] = Cases("quit-confirmation", "AppCommandChecks"),
        ["--conformance"] = Cases("boundaries", "action-icons", "tab-names", "ci-coverage"),
        ["--connection-errors"] = Cases("ShellChecks"),
        ["--content"] = Cases("content", "content-ui", "resources"),
        ["--context"] = Cases("context-consent", "air-context-lifecycle", "air-context-migration", "air-context-project"),
        ["--context-ui"] = Cases("context-consent-ui"),
        ["--create-project"] = Cases("CreateProjectChecks"),
        ["--design"] = Cases("design", "design-roles", "ThemeE2E"),
        ["--dialog-overlay"] = Cases("dialog-overlay"),
        ["--diff-virtualization"] = Cases("DiffVirtualizationChecks", "ChangesReviewChecks"),
        ["--discord"] = Cases("DiscordChecks-RunHostAsync", "DiscordChecks-RunUi"),
        ["--dock-input"] = Cases("DockInputChecks"),
        ["--documents"] = Cases("documents"),
        ["--drawers"] = Cases("ShellChecks"),
        ["--drawn-caret"] = Cases("drawn-rendering"),
        ["--drawn-rendering"] = Cases("drawn-rendering"),
        ["--drawn-trees"] = Cases("drawn-rendering"),
        ["--editor"] = Cases("open-world", "file-saving", "EditorChecks", "EditorTextChecks", "EditorWorkbenchChecks", "editor-settings", "EditorE2E"),
        ["--environment"] = Cases("environment"),
        ["--epub"] = Cases("epub-reader"),
        ["--fake-agent-launch"] = new Exempt("Fixture, not a check: a fake agent that sleeps 30 s for the agent-launch checks to start"),
        ["--fake-lsp"] = new Exempt("Fixture, not a check: the fake language server the language checks start"),
        ["--fake-lsp-push"] = new Exempt("Fixture, not a check: the fake language server that pushes diagnostics"),
        ["--fake-stderr-flood"] = new Exempt("Fixture, not a check: the language server that floods stderr for language-exit"),
        ["--file-drop"] = Cases("file-drop-host", "file-drop-ui", "file-drop-gestures"),
        ["--file-icons"] = Cases("FileIconsChecks-Host", "FileIconsChecks-UiChecks"),
        ["--file-transfer-actions"] = Cases("file-transfer-actions"),
        ["--file-watch"] = Cases("workspace-watch"),
        ["--files"] = Cases("FilesE2E", "projects", "watchers", "LiveRefreshE2E"),
        ["--files-modes"] = Cases("files-modes-ui"),
        ["--frontmatter"] = Cases("MarkdownDocumentE2E"),
        ["--gc-settings"] = Cases("gc-settings"),
        ["--spelling"] = Cases("spelling"),
        ["--ghostty-skia"] = Cases("GhosttySkiaChecks"),
        ["--git-host"] = Cases("git-host", "ChangesScopeE2E"),
        ["--github"] = Cases("github"),
        ["--github-browser"] = Cases("github-browser"),
        ["--github-tab-close"] = Cases("GitHubTabCloseE2E"),
        ["--gitless-panes"] = Cases("gitless-panes"),
        ["--headless"] = Cases("headless"),
        ["--headscale"] = new Exempt("Opt-in and slow, never in the argument-free suite (docs/develop/agent-reference.md): needs a throwaway Headscale built by scripts/build-headscale-test.cs"),
        ["--host-state"] = Cases("host-state", "state", "project-paths"),
        ["--intellij-license"] = Cases("intellij-license"),
        ["--intellij-license-mount"] = Cases("intellij-license-mount"),
        ["--intellij-license-ui"] = Cases("intellij-license-ui"),
        ["--intellij-maven"] = Cases("intellij-maven"),
        ["--language-stall"] = Cases("language-stall"),
        ["--keymap"] = Cases("keymap"),
        ["--linux"] = new Job("ci.yml", "check:packaged -- --linux"),
        ["--live-diffs"] = Cases("RenderedDiffE2E"),
        ["--browser-pairing"] = Cases("browser-pairing"),
        ["--local-browser-approval"] = Cases("local-browser-approval", "LocalBrowserApprovalE2E"),
        ["--local-browser-fixture"] = new Job("slow.yml", "browser check"),
        ["--lock-script"] = Cases("lock-script"),
        ["--loopback-terminals"] = Cases("loopback-terminals"),
        ["--lsp"] = Cases("language-services"),
        ["--lsp-catalog"] = Cases("language-catalog"),
        ["--lsp-dependencies"] = Cases("lsp-dependencies"),
        ["--lsp-icons"] = Cases("lsp-settings"),
        ["--lsp-limits"] = Cases("language-limits", "language-limits-ui"),
        ["--lsp-narrow"] = Cases("language-editor"),
        ["--lsp-on-demand"] = Cases("language-on-demand"),
        ["--lsp-real"] = new Exempt("Installs and starts real language servers into the shared cache (needs network and tools); no CI job provides them"),
        ["--lsp-real-csharp"] = new Exempt("Starts the real C# language server and restores a real solution (needs network and the installed server); no CI job provides them"),
        ["--lsp-refactoring"] = Cases("language-refactoring"),
        ["--lsp-settings"] = Cases("lsp-settings"),
        ["--lsp-ui"] = Cases("language-editor"),
        ["--managed-artifacts"] = Cases("managed-language-servers"),
        ["--managed-runtimes"] = Cases("managed-runtimes"),
        ["--markdown"] = Cases("MarkdownLinksE2E", "MarkdownAlertsE2E", "MarkdownDocumentE2E", "markdown-html-tables"),
        ["--markdown-chunks"] = Cases("drawn-rendering"),
        ["--markdown-code"] = Cases("drawn-rendering"),
        ["--markdown-find"] = Cases("MarkdownDocumentE2E"),
        ["--markdown-images"] = Cases("markdown-images"),
        ["--markdown-link-layout"] = Cases("drawn-rendering"),
        ["--markdown-tables"] = Cases("markdown-table-diffs", "markdown-mermaid-diffs"),
        ["--menu-layout"] = Cases("MenuLayoutChecks", "menu-layout-real"),
        ["--menu-popups"] = Cases("menu-popups"),
        ["--mermaid-stroke"] = Cases("mermaid-stroke"),
        ["--native-direct"] = new Exempt("opens a real window and takes focus; the repo runs it on macOS by hand, never on a developer machine or in the gate (docs/develop/checks.md, .github/workflows/slow.yml)"),
        ["--native-editor-find"] = new Exempt("Real macOS window and takes focus (NativeEditorFindChecks starts a classic desktop lifetime); run by hand, never in the gate"),
        ["--native-file-links"] = new Exempt("opens a real window and takes focus; the repo runs it on macOS by hand, never on a developer machine or in the gate (docs/develop/checks.md, .github/workflows/slow.yml)"),
        ["--native-frame-pump"] = new Exempt("Benchmark of Metal frame notifications in a real window (docs/develop/agent-reference.md: \"Benchmark\")"),
        ["--native-fsevents"] = new Exempt("macOS only: needs the DYLD_INSERT_LIBRARIES probe that scripts/mise run check:fsevents builds and injects (scripts/tasks/check-fsevents.cs); no CI job runs that task yet"),
        ["--native-osc52"] = new Exempt("opens a real window and takes focus; the repo runs it on macOS by hand, never on a developer machine or in the gate (docs/develop/checks.md, .github/workflows/slow.yml)"),
        ["--native-prompts"] = new Exempt("opens a real window and takes focus; the repo runs it on macOS by hand, never on a developer machine or in the gate (docs/develop/checks.md, .github/workflows/slow.yml)"),
        ["--native-scroll"] = new Exempt("opens a real window and takes focus; the repo runs it on macOS by hand, never on a developer machine or in the gate (docs/develop/checks.md, .github/workflows/slow.yml)"),
        ["--native-skia"] = new Exempt("opens a real window and takes focus; the repo runs it on macOS by hand, never on a developer machine or in the gate (docs/develop/checks.md, .github/workflows/slow.yml)"),
        ["--native-surface-memory"] = new Exempt("Measurement of IOSurface memory in a real window (docs/develop/agent-reference.md: \"Measurement\"); asserts only that it could measure"),
        ["--native-terminal"] = new Job("slow.yml", "check -- --native-terminal"),
        ["--native-terminal-links"] = new Exempt("opens a real window and takes focus; the repo runs it on macOS by hand, never on a developer machine or in the gate (docs/develop/checks.md, .github/workflows/slow.yml)"),
        ["--native-texture"] = new Job("slow.yml", "check -- --native-texture"),
        ["--new-workspace"] = Cases("NewWorkspaceShortcutE2E", "NewWorkspaceE2E"),
        ["--notifications"] = Cases("away-notifications"),
        ["--number-box-centering"] = Cases("number-box-centering"),
        ["--omp"] = Cases("omp-host", "omp-ui"),
        ["--openai"] = Cases("openai"),
        ["--owned-processes"] = Cases("owned-processes"),
        ["--packaged"] = new Job("ci.yml", "check:packaged -- --packaged"),
        ["--pane-retarget"] = Cases("pane-retarget"),
        ["--pane-retention"] = Cases("StartupChecks"),
        ["--pdf-preview"] = Cases("PdfPreviewChecks-Run"),
        ["--persisted-json"] = Cases("persisted-json"),
        ["--pi"] = Cases("pi", "pi-pane"),
        ["--plugin-channel"] = Cases("plugin-channel"),
        ["--plugin-crossing"] = Cases("plugin-crossing"),
        ["--plugin-isolated-ui"] = Cases("plugin-isolated-ui"),
        ["--plugin-json"] = Cases("plugin-json"),
        ["--plugin-lock-order"] = Cases("plugin-host"),
        ["--plugin-surface"] = Cases("plugin-surface"),
        ["--plugin-surface-remote"] = Cases("plugin-surface-remote"),
        ["--plugins"] = Cases("plugin-host", "plugin-json", "spec-dialect", "review-plugin", "blueprint", "claude-code", "DiscordChecks-RunHostAsync", "workspace-watch", "BranchGraphChecks-RunHostAsync", "processes", "VisualizeChecks-Host", "FileIconsChecks-Host", "codex-host", "plugin-ui", "ProcessesPaneChecks", "file-actions", "plugin-watch", "spec-dialect-ui", "review-plugin-ui", "blueprint-ui", "claude-code-ui", "claude-code-launcher", "DiscordChecks-RunUi", "PdfPreviewChecks-Run", "epub-reader", "BranchGraphChecks-RunUi", "visualize-ui", "FileIconsChecks-UiChecks", "codex-ui", "away-notifications"),
        ["--plugins-settings"] = Cases("plugins-settings"),
        ["--process"] = Cases("process"),
        ["--processes"] = Cases("processes", "ProcessesPaneChecks", "processes-settings-ui"),
        ["--project-close"] = Cases("terminal-hosts", "ProjectContextE2E", "ProjectPinE2E"),
        ["--project-host"] = Cases("projects"),
        ["--project-language-file"] = Cases("project-language-file"),
        ["--project-toml"] = Cases("project-toml"),
        ["--providers"] = Cases("providers"),
        ["--providers-ui"] = Cases("providers-ui"),
        ["--pty-output-allocations"] = Cases("pty-output-allocations"),
        ["--rail"] = Cases("RailRetentionChecks", "CrossProjectTabsE2E"),
        ["--rail-grouping"] = Cases("RailGroupingE2E", "RailEdgePillE2E"),
        ["--reconnect"] = Cases("reconnect", "ReconnectE2E"),
        ["--registry"] = Cases("registry", "ExternalWorkspaceE2E"),
        ["--rename-follows"] = Cases("rename-follows"),
        ["--rendering-bench"] = new Exempt("Benchmark (RenderingBenchmarks), prints timings and asserts nothing about behavior"),
        ["--results-selftest"] = Cases("results"),
        ["--review"] = Cases("ReviewChecks"),
        ["--review-plugin"] = Cases("review-plugin", "review-plugin-ui", "layout", "gitless-panes", "StartupChecks"),
        ["--review-presentation"] = Cases("ChangesReviewChecks"),
        ["--review-retention"] = Cases("ChangesReviewChecks"),
        ["--rpc-codec"] = Cases("rpc-codec"),
        ["--run-file"] = Cases("run-file"),
        ["--runner"] = Cases("runner"),
        ["--runtimes"] = Cases("runtimes"),
        ["--runtimes-ui"] = Cases("runtimes-ui"),
        ["--scintilla-core"] = Cases("scintilla-core"),
        ["--scratch"] = Cases("layout", "projects", "TerminalChromeChecks", "WorkspaceTabsE2E", "MarkdownDocumentE2E", "LayoutE2E", "NavigationChecks", "StartupChecks", "DockInputChecks"),
        ["--section-headers"] = Cases("section-headers"),
        ["--secure-input"] = new Matrix("terminals"),
        ["--serving"] = Cases("host-listener", "HostServingE2E"),
        ["--settings"] = Cases("SettingsGitHubE2E"),
        ["--settings-navigation"] = Cases("settings-navigation"),
        ["--settings-terms-status"] = Cases("settings-terms-status"),
        ["--settings-terms-ui"] = Cases("settings-terms-ui"),
        ["--settings-touch"] = Cases("settings-touch"),
        ["--shared-controls"] = Cases("SharedControlChecks"),
        ["--shared-terminals"] = Cases("TerminalsE2E"),
        ["--shell"] = Cases("ShellChecks"),
        ["--shell-integration"] = new Matrix("terminals"),
        ["--specs"] = Cases("specs"),
        ["--specs-panel"] = Cases("SpecsPanelE2E", "WelcomeE2E"),
        ["--stage-packaged"] = new Exempt("Helper, not a check: copies the check files next to a packaged app (scripts/tasks/check-packaged.cs)"),
        ["--startup"] = Cases("StartupChecks"),
        ["--state"] = Cases("state"),
        ["--switch"] = Cases("plugin-ui"),
        ["--switch-latency"] = new Exempt("Wall-clock p90 budget (100 ms, TEREDITOR_SWITCH_BUDGET_MS) that a shared CI runner stretches; --workspace-switch counts host calls instead and is in the gate"),
        ["--sync"] = new Matrix("sync"),
        ["--syntax"] = Cases("SyntaxHighlightChecks"),
        ["--tab-overflow"] = Cases("LayoutE2E"),
        ["--tailscale"] = Cases("tailscale", "tailscale-ui"),
        ["--tereditor-exclude"] = Cases("tereditor-exclude", "tereditor-exclude-ui"),
        ["--terminal-badges"] = Cases("terminal-links-ui"),
        ["--terminal-directory"] = Cases("TerminalsE2E"),
        ["--terminal-links"] = Cases("terminal-links", "terminal-links-ui", "GhosttySkiaChecks"),
        ["--terminal-relay"] = new Exempt("Fixture, not a check: the relay child process that the remote terminal checks start"),
        ["--terminal-replay"] = Cases("terminal-hosts"),
        ["--terminal-resume-hint"] = Cases("TerminalResumeHintChecks"),
        ["--terminal-signals"] = Cases("terminal-hosts", "terminal-signals-ui"),
        ["--terminal-surfaces"] = Cases("GhosttySkiaChecks"),
        ["--terminal-watch-replay"] = Cases("terminal-hosts"),
        ["--terminals"] = new Matrix("terminals"),
        ["--texture-fallback"] = new Exempt("opens a real window and takes focus; the repo runs it on macOS by hand, never on a developer machine or in the gate (docs/develop/checks.md, .github/workflows/slow.yml)"),
        ["--transport"] = Cases("browser-transport", "websocket-flow"),
        ["--tree-fixture"] = new Exempt("Fixture, not a check: the process tree that the runner case starts"),
        ["--tree-fixture-leaf"] = new Exempt("Fixture, not a check: the leaf of the runner case process tree"),
        ["--ui"] = Cases("workbench"),
        ["--ui-errors"] = Cases("ui-errors"),
        ["--ui-smoke"] = Cases("workbench"),
        ["--unified-chrome"] = Cases("ShellChecks"),
        ["--update"] = new Exempt("Flag, not a mode: with --rpc-codec or --persisted-json it rewrites the golden files"),
        ["--vertical-tabs"] = Cases("VerticalTabsE2E"),
        ["--visualize"] = Cases("VisualizeChecks-Host", "visualize-ui", "MarkdownMermaidE2E"),
        ["--visualize-host"] = Cases("VisualizeChecks-Host"),
        ["--visualize-selection"] = Cases("visualize-selection"),
        ["--watcher-hygiene"] = Cases("watcher-hygiene"),
        ["--watchers"] = Cases("watchers"),
        ["--welcome"] = Cases("WelcomeE2E"),
        ["--windows"] = new Job("ci.yml", "--windows"),
        ["--workspace-clone"] = Cases("workspace-clone"),
        ["--workspace-create-stall"] = new Exempt("Wall-clock budget (50 ms longest dispatcher wait, TEREDITOR_CREATE_STALL_BUDGET_MS) that a shared CI runner stretches; flaky checks are not tolerated (AGENTS.md)"),
        ["--workspace-delete"] = Cases("WorkspaceResponsivenessE2E"),
        ["--workspace-emigrate"] = Cases("workspace-emigrate"),
        ["--workspace-switch"] = Cases("workspace-switch"),
        ["--workspace-tools"] = Cases("workspace-tools", "workspace-tools-ui"),
        ["--workspace-views"] = Cases("workspace-views"),
        ["--workspaces"] = new Matrix("workspaces"),
        ["--write"] = new Exempt("Flag, not a mode: with --design it regenerates src/Tereditor.Plugins.UI.Kit/Generated from the authored design sources"),
        ["--zig-deps"] = Cases("zig-deps"),
    };

    [GeneratedRegex("\"(--[a-z0-9-]+)\"")]
    private static partial Regex Literal();

    [GeneratedRegex("Gate\\.Case\\(\"([^\"]+)\"")]
    private static partial Regex CaseName();

    [GeneratedRegex("mode:\\s*\\[([^\\]]*)\\]")]
    private static partial Regex MatrixModes();

    /// <summary>The mode names written on the condition lines of the mode switch (Program.cs) and of the runner's Main (Runner.cs).</summary>
    internal static SortedSet<string> ModesIn(string program, string runner)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var switchEnd = program.IndexOf("Gate.Case(\"packaged-app\"", StringComparison.Ordinal);
        var mainStart = runner.IndexOf("public static int Main", StringComparison.Ordinal);
        var mainEnd = runner.IndexOf("RunnerOptions options;", StringComparison.Ordinal);
        if (switchEnd < 0 || mainStart < 0 || mainEnd < mainStart) throw new InvalidOperationException("The mode switch or the runner's Main moved; update CiCoverageChecks.ModesIn.");
        foreach (var region in new[] { program[..switchEnd], runner[mainStart..mainEnd] })
            foreach (var line in region.Split('\n'))
            {
                var text = line.TrimStart();
                if (!text.StartsWith("if (args", StringComparison.Ordinal) && !text.StartsWith("if (!args", StringComparison.Ordinal)) continue;
                foreach (Match literal in Literal().Matches(text)) found.Add(literal.Groups[1].Value);
            }
        return found;
    }

    /// <summary>Everything that is wrong between the modes, the table, the registered gate cases and the workflows; empty when it all agrees.</summary>
    internal static List<string> Verify(IReadOnlySet<string> modes, IReadOnlyDictionary<string, Cover> table, IReadOnlySet<string> cases, Func<string, string?> workflow)
    {
        var problems = new List<string>();
        foreach (var mode in modes.Where(mode => !table.ContainsKey(mode)))
            problems.Add($"{mode} is a check mode with no CI decision: add a Gate.Case for it in Program.cs, run it in a CI job, or list it in CiCoverageChecks with the reason it stays out.");
        foreach (var mode in table.Keys.Where(mode => !modes.Contains(mode)))
            problems.Add($"{mode} is listed in CiCoverageChecks but no mode of that name exists any more.");
        foreach (var (mode, cover) in table)
        {
            switch (cover)
            {
                case Gated gated:
                    if (gated.Cases.Length == 0) problems.Add($"{mode} names no gate case.");
                    problems.AddRange(gated.Cases.Where(name => !cases.Contains(name)).Select(name => $"{mode} names the gate case {name}, which no Gate.Case registers."));
                    break;
                case Job job:
                    if (workflow(job.Workflow) is not { } text) problems.Add($"{mode} names the workflow {job.Workflow}, which does not exist.");
                    else if (!text.Contains(job.Needle, StringComparison.Ordinal)) problems.Add($"{mode} is said to run in {job.Workflow}, but it has no step containing \"{job.Needle}\".");
                    break;
                case Matrix matrix:
                    if (workflow("ci.yml") is not { } ci) problems.Add($"{mode} names the CI matrix, but ci.yml does not exist.");
                    else if (!MatrixModes().Matches(ci).Any(entry => entry.Groups[1].Value.Split(',').Select(name => name.Trim()).Contains(matrix.Mode)))
                        problems.Add($"{mode} is said to run as the CI matrix mode {matrix.Mode}, but no \"mode: [...]\" list in ci.yml has it.");
                    break;
                case Exempt exempt:
                    if (exempt.Reason.Length < 20) problems.Add($"{mode} is exempt without a reason.");
                    break;
            }
        }
        return problems;
    }

    internal static void Run()
    {
        SelfTest();
        if (BoundaryChecks.Checkout() is not { } checkout) { Console.WriteLine("SKIP ci coverage: no source checkout"); return; }
        var checks = Path.Combine(checkout, "tests", "Tereditor.Checks");
        var program = File.ReadAllText(Path.Combine(checks, "Program.cs"));
        var cases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { program, File.ReadAllText(Path.Combine(checks, "UiChecks.cs")) })
            foreach (Match match in CaseName().Matches(file)) cases.Add(match.Groups[1].Value);
        var modes = ModesIn(program, File.ReadAllText(Path.Combine(checks, "Runner.cs")));
        var problems = Verify(modes, Modes, cases, name =>
        {
            var path = Path.Combine(checkout, ".github", "workflows", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        });
        // A slow case leaves the merge gate, never the suite: a scheduled run and a dispatchable one must take the Slow lane, and the gate must take the Merge lane.
        if (File.ReadAllText(Path.Combine(checks, "UiChecks.cs")).Contains("slow: true", StringComparison.Ordinal) || program.Contains("slow: true", StringComparison.Ordinal))
            foreach (var (name, lane) in new[] { ("nightly.yml", "--lane slow"), ("slow.yml", "--lane slow"), ("ci.yml", "--lane merge") })
                if (!File.Exists(Path.Combine(checkout, ".github", "workflows", name)) || !File.ReadAllText(Path.Combine(checkout, ".github", "workflows", name)).Contains(lane, StringComparison.Ordinal))
                    problems.Add($"Gate cases are marked slow, but {name} has no \"{lane}\" run: they would never run.");
        if (problems.Count > 0) throw new InvalidOperationException("Check modes and CI disagree:\n" + string.Join('\n', problems));
        Console.WriteLine($"PASS ci coverage: {modes.Count} modes, {Modes.Values.OfType<Gated>().Count()} in the gate, {Modes.Values.OfType<Job>().Count() + Modes.Values.OfType<Matrix>().Count()} in a CI job, {Modes.Values.OfType<Exempt>().Count()} exempt");
    }

    // The verifier must fail on the mistakes it exists for.
    private static void SelfTest()
    {
        var table = new Dictionary<string, Cover> { ["--a"] = new Gated(["a"]), ["--b"] = new Exempt("a fixture, not a check of the product") };
        var cases = new HashSet<string> { "a" };
        Func<string, string?> none = _ => null;
        Require(Verify(new HashSet<string> { "--a", "--b" }, table, cases, none).Count == 0, "A fully decided mode set passes.");
        Require(Verify(new HashSet<string> { "--a", "--b", "--c" }, table, cases, none) is [var added] && added.StartsWith("--c ", StringComparison.Ordinal), "A mode with no decision fails and is named.");
        Require(Verify(new HashSet<string> { "--a" }, table, cases, none) is [var stale] && stale.StartsWith("--b ", StringComparison.Ordinal), "An entry for a removed mode fails.");
        Require(Verify(new HashSet<string> { "--a", "--b" }, table, new HashSet<string>(), none) is [var unregistered] && unregistered.Contains("no Gate.Case", StringComparison.Ordinal), "A gate case nobody registers fails.");
        var job = new Dictionary<string, Cover> { ["--j"] = new Job("ci.yml", "-- --j"), ["--m"] = new Matrix("m") };
        Require(Verify(new HashSet<string> { "--j", "--m" }, job, cases, _ => "run: x -- --j\n mode: [m, n]").Count == 0, "A job and a matrix mode that the workflow names pass.");
        Require(Verify(new HashSet<string> { "--j", "--m" }, job, cases, _ => "mode: [n]").Count == 2, "A workflow that does not mention the mode fails.");
        Require(ModesIn("if (args.SequenceEqual([\"--x\"]))\nGate.Case(\"packaged-app\", x);", "public static int Main()\n if (args.Contains(\"--y\"))\nRunnerOptions options;").SequenceEqual(["--x", "--y"]), "Modes are read from condition lines of both files.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}