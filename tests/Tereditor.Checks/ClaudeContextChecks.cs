using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;

using Tereditor.Checks.E2E;
using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.ClaudeCode;
using Tereditor.Plugins.ClaudeCode.Host;
using Tereditor.Plugins.ClaudeCode.Host.ClaudeConfig;
using Tereditor.Plugins.ClaudeCode.UI;

namespace Tereditor.Checks;

/// <summary>The Context surface's <c>/context</c> baseline: the report parser, the runner, the appended prompt difference, its triggers, live usage and the table rows.</summary>
internal static class ClaudeContextChecks
{
    // A trimmed capture of `claude /context --output-format json`'s result, with the paths made up.
    public const string Report = """
        ## Context Usage

        **Model:** claude-opus-5-5
        **Tokens:** 16.6k / 1m (2%)

        ### Estimated usage by category

        | Category | Tokens | Percentage |
        |----------|--------|------------|
        | System prompt | 2.5k | 0.3% |
        | System tools | 4.3k | 0.4% |
        | MCP tools | 671 | 0.1% |
        | MCP server instructions | 717 | 0.1% |
        | MCP tools (deferred) | 1.4k | 0.1% |
        | System tools (deferred) | 13.5k | 1.3% |
        | Memory files | 2.2k | 0.2% |
        | Skills | 6.2k | 0.6% |
        | Messages | 10 | 0.0% |
        | Free space | 950.4k | 95.0% |
        | Autocompact buffer | 33k | 3.3% |

        ### MCP Tools

        | Tool | Server | Tokens |
        |------|--------|--------|
        | mcp__docs__create | docs | 256 |
        | mcp__docs__read | docs | 324 |
        | mcp__docs__update | docs | 1.4k |
        | mcp__notes__find | notes | 91 |

        ### Memory Files

        | Type | Path | Tokens |
        |------|------|--------|
        | User | /home/user/.claude/CLAUDE.md | 2.2k |

        ### Skills

        | Skill | Source | Tokens |
        |-------|--------|--------|
        | example:review | Plugin (example) | ~150 |
        | tidy | Built-in | < 20 |
        | dataviz | Built-in | ~480 |
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static ClaudeContextBreakdown Fixture() => ContextReport.Parse(Report)!;

    public static async Task Host(string directory)
    {
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new { type = "result", result = Report, total_cost_usd = 0 });
        var parsed = ContextReport.ParseJson(json);
        Require(parsed is { Model: "claude-opus-5-5", Used: 16_600, Window: 1_000_000 } && parsed.Categories.Count == 11 &&
            parsed.Categories[3] == new ClaudeContextCategory("MCP server instructions", 717, 0.1) && parsed.Categories[^1].Tokens == 33_000,
            "The /context report's header and category table parse in Claude Code's order.");
        Require(parsed!.McpTools.Count == 4 && parsed.McpTools[0] == new ClaudeContextItem("mcp__docs__create", "docs", 256) &&
            parsed.MemoryFiles.Single() == new ClaudeContextItem("/home/user/.claude/CLAUDE.md", "User", 2_200) &&
            parsed.Skills.Select(skill => skill.Tokens).SequenceEqual([150L, 20, 480]),
            "MCP tools, memory files and skills parse with approximate counts (~, <) read as numbers.");
        // /context's tool table lists every MCP tool, deferred ones included (checked against Claude Code 2.1.325), so
        // it sums to the "MCP tools" category plus "MCP tools (deferred)", not to the loaded category alone.
        Require(parsed.McpTools.Sum(tool => tool.Tokens) == parsed.Categories.Where(category => category.Name.StartsWith("MCP tools", StringComparison.Ordinal)).Sum(category => category.Tokens),
            "The MCP tool table covers loaded and deferred tools alike.");
        Require(ContextReport.ParseJson("{\"result\":\"Unknown command\"}") is null && ContextReport.ParseJson("not json") is null,
            "Output without a context report is refused, not misread.");

        // Memory paths are matched to the pane's rows: ~ expanded, and a link matched through its target.
        var home = Directory.CreateDirectory(Path.Combine(directory, "home")).FullName;
        var real = Path.Combine(home, "real.md");
        File.WriteAllText(real, "x");
        var link = Path.Combine(home, "CLAUDE.md");
        File.CreateSymbolicLink(link, real);
        var memory = parsed with { MemoryFiles = [new("~/x.md", "User", 1), new(real, "Project", 2), new("rules/a.md", "Project", 3)] };
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);
        try
        {
            var normal = ContextReport.Normalise(memory, home, [link]).MemoryFiles.Select(file => file.Name).ToArray();
            Require(normal.SequenceEqual([Path.Combine(home, "x.md"), link, Path.Combine(home, "rules", "a.md")]),
                "Memory file paths are expanded, made absolute and matched through links to the pane's rows. Got: " + string.Join(", ", normal));
        }
        finally { Environment.SetEnvironmentVariable("HOME", previousHome); }

        var flags = ContextLaunch.Flags("claude --append-system-prompt-file " + Path.Combine(directory, "missing.md") +
            " --plugin-dir /p --add-dir /s --model opus --effort=high --resume abc --strict-mcp-config 'do it'", directory);
        Require(flags.SequenceEqual(["--plugin-dir", "/p", "--add-dir", "/s", "--model", "opus", "--effort", "high", "--strict-mcp-config"]),
            "Only context-shaping launch flags carry over; resume and the prompt are dropped. Got: " + string.Join(' ', flags));

        Require(ContextLaunch.Flags("claude --disallowedTools=EnterWorktree --model opus", directory).SequenceEqual(["--disallowedTools", "EnterWorktree", "--model", "opus"]),
            "A measured baseline carries the worktree denial of the launch.");
        ToolPolicy();

        // The fake CLI is a POSIX shell script.
        if (!OperatingSystem.IsWindows()) await Runner(directory, json);
        LiveUsage(directory);
        Console.WriteLine("PASS Claude Code context: /context parsing, background runner, appended prompt difference, debounced triggers and live usage");
    }

    // Every launch path asks the one policy: terminal lines (plain, resume, fork, continue, wrapped by a provider's settings), merged with the user's own flags.
    private static void ToolPolicy()
    {
        var provider = new AgentLaunchContribution { Arguments = ["--settings", "/p.json"], ConflictingArguments = ["--settings"] };
        foreach (var args in new[] { "", "--continue", "--resume abc", "--resume abc --fork-session", "'a prompt'" })
        {
            var line = ClaudeLaunch.SessionCommand("claude", args, contributions: [provider], sessionId: "s");
            Require(line.Split(' ').Count(word => word == ClaudeToolPolicy.Flag) == 1, "A terminal line denies EnterWorktree once: " + line);
        }
        Require(ClaudeToolPolicy.FlagFor(["--disallowedTools=Bash"]) == ClaudeToolPolicy.Flag && ClaudeToolPolicy.FlagFor(["--disallowed-tools", "Bash,Read"]) == ClaudeToolPolicy.Flag,
            "Another denial of the user's is merged: the flag repeats and the CLI joins them.");
        Require(ClaudeToolPolicy.FlagFor(["--disallowedTools", "Bash", "EnterWorktree"]) is null && ClaudeToolPolicy.FlagFor(["--disallowedTools=Bash,EnterWorktree"]) is null,
            "A user's own denial of the tool is not repeated.");
        Require(ClaudeToolPolicy.FlagFor(["--tools", "Bash,Read"]) is null && ClaudeToolPolicy.FlagFor(["--tools=default"]) == ClaudeToolPolicy.Flag,
            "An explicit tool list without the tool needs no denial; the default set does.");
        var own = ClaudeLaunch.SessionCommand("claude --disallowedTools=Bash", "--disallowedTools EnterWorktree");
        Require(!own.Contains(ClaudeToolPolicy.Flag, StringComparison.Ordinal), "A line that already denies the tool is left as it is: " + own);
        var merged = ClaudeLaunch.SessionCommand("claude --disallowedTools=Bash", "");
        Require(merged.Contains("--disallowedTools=Bash", StringComparison.Ordinal) && merged.Contains(ClaudeToolPolicy.Flag, StringComparison.Ordinal), "The denial merges with the setting's own: " + merged);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task Runner(string directory, string json)
    {
        // A fake claude that logs its directory and arguments, sleeps when asked and answers with the fixture.
        var bin = Path.Combine(directory, "fake-claude");
        var log = Path.Combine(directory, "fake-claude.log");
        var answer = Path.Combine(directory, "answer.json");
        File.WriteAllText(answer, json);
        File.WriteAllText(bin, $"#!/bin/sh\necho \"$(pwd -P)|$*|$FAKE_MARK\" >> '{log}'\n[ -n \"$FAKE_SLEEP\" ] && sleep \"$FAKE_SLEEP\"\ncat '{answer}'\n");
        File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var cwd = Directory.CreateDirectory(Path.Combine(directory, "cwd")).FullName;
        var input = Path.Combine(cwd, "CLAUDE.md");
        File.WriteAllText(input, "one\n");
        var finished = 0;
        using var runner = new ContextUsageRunner((spec, timeout, token) => Bounded.RunAsync(spec.Argv, timeout, spec.Cwd, spec.Environment, token),
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300), _ => Interlocked.Increment(ref finished));
        ContextRunSpec Spec(string? sleep = null) => new("workspace", bin, cwd, ["--model", "opus"],
            new Dictionary<string, string> { ["FAKE_MARK"] = "env", ["FAKE_SLEEP"] = sleep ?? "" }, [input]);

        var first = runner.Get(Spec(), refresh: false);
        var second = runner.Get(Spec(), refresh: true);
        Require(first.Running && second.Running && runner.Runs == 1, "A baseline measures in the background, one run at a time per directory and flags.");
        await runner.IdleAsync();
        var lines = File.ReadAllLines(log);
        Require(lines.Length == 1 && lines[0].EndsWith("/cwd|/context --output-format json --no-session-persistence --model opus|env", StringComparison.Ordinal),
            "The run happens in the tab's directory with its flags and environment, without session persistence. Got: " + string.Join('\n', lines));
        var cached = runner.Get(Spec(), refresh: false);
        Require(!cached.Running && cached.Breakdown?.Window == 1_000_000 && cached.MeasuredAt is not null && runner.Runs == 1 && finished == 1,
            "An unchanged baseline is served from the cache and announced once.");

        // A burst of changes to an input runs once, after the burst is quiet.
        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(input, "changed " + index + new string('x', index) + "\n");
            runner.Changed([input]);
            await Task.Delay(50);
        }
        runner.Changed([Path.Combine(cwd, "unrelated.txt")]);
        await Task.Delay(800);
        await runner.IdleAsync();
        Require(runner.Runs == 2 && File.ReadAllLines(log).Length == 2, "A debounced burst of input changes re-runs the baseline exactly once.");
        runner.Changed([input]);
        await Task.Delay(800);
        await runner.IdleAsync();
        Require(runner.Runs == 2, "A change notice whose file did not actually change starts no run.");

        // An appended prompt is measured as the System prompt difference against the same run without it; editing the
        // appended file re-runs only the run that reads it.
        var appendix = Path.Combine(directory, "appendix.md");
        File.WriteAllText(appendix, "appended\n");
        File.WriteAllText(Path.Combine(directory, "appended.json"), json.Replace("| System prompt | 2.5k |", "| System prompt | 3.8k |", StringComparison.Ordinal));
        File.WriteAllText(bin, $"#!/bin/sh\necho \"$(pwd -P)|$*|$FAKE_MARK\" >> '{log}'\n[ -n \"$FAKE_SLEEP\" ] && sleep \"$FAKE_SLEEP\"\n" +
            $"case \"$*\" in *--append-system-prompt-file*) cat '{Path.Combine(directory, "appended.json")}' ;; *) cat '{answer}' ;; esac\n");
        var withAppendix = Spec() with { Flags = ["--append-system-prompt-file", appendix, "--model", "appended"], Inputs = [input, appendix] };
        var before = File.ReadAllLines(log).Length;
        runner.Get(withAppendix, refresh: false);
        await runner.IdleAsync();
        var measuredAppendix = runner.Get(withAppendix, refresh: false).Breakdown;
        var pair = File.ReadAllLines(log)[before..];
        Require(measuredAppendix?.AppendedPromptTokens == 1_300 && pair.Length == 2 &&
            pair.Count(line => line.Contains("--append-system-prompt-file", StringComparison.Ordinal)) == 1,
            "The appended prompt costs the System prompt difference of two runs, one without the file. Got: " + measuredAppendix?.AppendedPromptTokens + "\n" + string.Join('\n', pair));
        File.WriteAllText(appendix, "appended, and longer now\n");
        runner.Changed([appendix]);
        await Task.Delay(800);
        await runner.IdleAsync();
        Require(File.ReadAllLines(log).Length == before + 3, "Editing the appended file re-runs only the run with it; the bare run stays cached.");

        var slow = Spec("10") with { Flags = ["--model", "slow"] };
        runner.Get(slow, refresh: false);
        await runner.IdleAsync();
        var timedOut = runner.Get(slow, refresh: false);
        Require(timedOut is { Running: false, Breakdown: null } && timedOut.Error?.Contains("did not finish", StringComparison.Ordinal) == true,
            "A run past its deadline is killed and reported as an error.");
        var cancelled = Spec("10") with { Flags = ["--model", "cancel"] };
        runner.Get(cancelled, refresh: false);
        runner.Dispose();
        var started = DateTime.UtcNow;
        await runner.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Require(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "Disposing the runner cancels a run under way.");

    }

    private static void LiveUsage(string directory)
    {
        // Live usage: the main transcript's latest assistant message, input and cache together.
        var transcript = Path.Combine(directory, "live.jsonl");
        static string Turn(string id, int input, int read, int write) => JsonSerializer.Serialize(new
        { type = "assistant", message = new { id, usage = new { input_tokens = input, output_tokens = 1, cache_read_input_tokens = read, cache_creation_input_tokens = write } } }) + "\n";
        File.WriteAllText(transcript, Turn("a", 5, 1000, 200) + Turn("b", 7, 1200, 30));
        var usage = new TranscriptUsage();
        Require(usage.Read(transcript).Context == 1237, "Live context is the latest message's input plus cache reads and writes.");
        File.AppendAllText(transcript, Turn("c", 3, 1300, 0));
        Require(usage.Read(transcript).Context == 1303, "Live context follows an appended turn with a tail read.");
    }

    private static void AutoRefresh()
    {
        var clock = DateTimeOffset.UtcNow;
        var visible = true; var running = false; DateTimeOffset? last = clock; var refreshes = 0;
        var auto = new ClaudeAutoRefresh(() => clock, () => visible, () => running, () => last, () => { refreshes++; last = clock; });
        Require(!auto.Tick() && refreshes == 0, "A fresh reading is not refreshed.");
        clock += ClaudeAutoRefresh.Interval - TimeSpan.FromMinutes(1);
        Require(!auto.Tick(), "A reading just under an hour old is not refreshed.");
        visible = false; clock += TimeSpan.FromMinutes(2);
        Require(!auto.Tick() && refreshes == 0, "A hidden pane is not refreshed.");
        visible = true; running = true;
        Require(!auto.Tick() && refreshes == 0, "No refresh starts while one is running.");
        running = false;
        Require(auto.Tick() && refreshes == 1 && !auto.Tick() && refreshes == 1, "The hourly tick refreshes exactly once.");
        last = null;
        Require(auto.Tick() && refreshes == 2, "A pane with no reading refreshes when first visible.");
    }

    public static void Ui()
    {
        AutoRefresh();
        // The baseline's rows: Claude's own prompt less the appendix, built-in tools, MCP tools by server, server
        // instructions and skills; memory files, deferred tools, the message, free space and the reserve are not rows.
        var rows = ClaudeConfigPanel.BaselineRows(Fixture(), claudePrompt: 1_300);
        Require(rows.Select(row => row.Key).SequenceEqual(["system-prompt", "system-tools", "mcp-tools", "mcp-instructions", "skills"]) &&
            rows[0].Tokens == 1_300 && rows.Sum(row => row.Tokens) == 1_300 + 4_300 + 671 + 717 + 6_200,
            "The baseline rows are the categories a fresh session pays for, the system prompt without the appended file. Got: " +
            string.Join(", ", rows.Select(row => row.Key + "=" + row.Tokens)));
        // Context-menu actions: only documented switches, none for the system prompt, and the way back for what is off.
        var none = new HashSet<string>();
        var skills = rows.Single(row => row.Key == "skills").Children;
        var dataviz = ClaudeConfigPanel.RowActions(skills.Single(row => row.Label == "dataviz"), none, none).Single();
        var reviewing = ClaudeConfigPanel.RowActions(skills.SelectMany(row => row.Children.Prepend(row)).Single(row => row.Key == "skills/example:review"), none, none);
        var docs = ClaudeConfigPanel.RowActions(rows.Single(row => row.Key == "mcp-tools").Children[0], none, none).Single();
        var docsTool = ClaudeConfigPanel.RowActions(rows.Single(row => row.Key == "mcp-tools").Children[0].Children[0], none, none).Single();
        var builtIn = rows.Single(row => row.Key == "system-tools").Children;
        Require(ClaudeConfigPanel.RowActions(rows[0], none, none).Count == 0 && ClaudeConfigPanel.RowActions(rows.Single(row => row.Key == "skills"), none, none).Count == 0 &&
            builtIn.Count > 0 && builtIn.All(tool => tool.Tokens < 0) && !builtIn.Any(tool => tool.Label is "EndConversation" or "ToolSearch"),
            "The system prompt and the group rows have no actions; built-in tools list one row each, with no size /context does not give.");
        Require(dataviz.Edit.Edit is SkillEdit { Name: "dataviz", Enabled: false } && dataviz.Label == "Turn off this skill" &&
            reviewing.Single().Edit.Edit is DenyToolEdit { Rule: "Skill(example:review)", Denied: true } &&
            docs.Edit.Edit is DenyToolEdit { Rule: "mcp__docs__*", Denied: true } && docs.Label == "Turn off this MCP server" &&
            docsTool.Edit.Edit is DenyToolEdit { Rule: "mcp__docs__update", Denied: true } &&
            ClaudeConfigPanel.RowActions(builtIn.Single(tool => tool.Label == "WebSearch"), none, none).Single().Edit.Edit is DenyToolEdit { Rule: "WebSearch", Denied: true },
            "A skill goes through skillOverrides, a plugin skill through a Skill() deny rule and no file edit, an MCP server or tool and a built-in tool through permissions.deny.");
        var off = new HashSet<string> { "mcp__docs__*", "WebSearch" };
        Require(ClaudeConfigPanel.RowActions(rows.Single(row => row.Key == "mcp-tools").Children[0], off, none).Single().Edit.Edit is DenyToolEdit { Denied: false } &&
            ClaudeConfigPanel.RowActions(builtIn.Single(tool => tool.Label == "WebSearch"), off, none).Single().Edit.Edit is DenyToolEdit { Denied: false } &&
            ClaudeConfigPanel.RowActions(skills.Single(row => row.Label == "dataviz"), none, new HashSet<string> { "dataviz" }).Single().Edit.Edit is SkillEdit { Enabled: true } &&
            ClaudeConfigPanel.ServerToolPrefix("my.server x") == "mcp__my_server_x",
            "What is already off offers to turn it back on.");
        // A skill's file: a user or project skill's folder, a plugin's skill from its declared file; none for a built-in one.
        var userSkill = new ClaudeCapability(ClaudeCapabilityKind.Skill, "tidy", new(ClaudeConfigScope.User, "/home/u/.claude/skills/tidy"), true);
        var plugin = new ClaudeCapability(ClaudeCapabilityKind.Plugin, "example@market", new(ClaudeConfigScope.User, "/home/u/.claude/settings.json"), true)
        { Parts = [new(ClaudePluginPartKind.Skill, "example:review") { Path = "/home/u/.claude/plugins/cache/example/skills/review/SKILL.md" }, new(ClaudePluginPartKind.Skill, "inline")] };
        var files = ClaudeConfigPanel.SkillFiles([userSkill, plugin]);
        Require(files["tidy"] == Path.Combine("/home/u/.claude/skills/tidy", "SKILL.md") && files["example:review"].EndsWith("review/SKILL.md", StringComparison.Ordinal) &&
            !files.ContainsKey("dataviz") && !files.ContainsKey("inline"), "Skill names resolve to their SKILL.md from the snapshot; a built-in or file-less skill has none.");
        var mcp = rows.Single(row => row.Key == "mcp-tools");
        Require(mcp.Children.Select(server => server.Label).SequenceEqual(["docs", "notes"]) && mcp.Children[0].Tokens == 1_980 &&
            mcp.Children[0].Children.Select(tool => tool.Label).SequenceEqual(["update", "read", "create"]),
            "MCP tools open into servers, heaviest first with subtotals, and each server into its tools without the prefix.");
        // The row counts what loads at start; a server's subtotal includes its deferred tools, so it says "if loaded".
        Require(mcp.Tokens == 671 && mcp.Children.All(server => server.Suffix == " if loaded" && server.Tip is not null) &&
            mcp.Tip?.Contains("1.4k tokens", StringComparison.Ordinal) == true,
            "With deferred MCP tools, servers read \"if loaded\" and the MCP tools row names the deferred total.");
        var undeferred = Fixture() with { Categories = [.. Fixture().Categories.Where(category => category.Name != "MCP tools (deferred)")] };
        Require(ClaudeConfigPanel.BaselineRows(undeferred, null).Single(row => row.Key == "mcp-tools").Children.All(server => server.Suffix == "" && server.Tip is null),
            "Without deferred MCP tools a server's subtotal is plain.");
        // Every MCP tool deferred, as /context prints it then: no "MCP tools" category, the table still lists each tool.
        var allDeferred = ContextReport.Parse(Report.Replace("| MCP tools | 671 | 0.1% |", "", StringComparison.Ordinal)
            .Replace("| MCP tools (deferred) | 1.4k |", "| MCP tools (deferred) | 2.1k |", StringComparison.Ordinal))!;
        var deferredRows = ClaudeConfigPanel.BaselineRows(allDeferred, null);
        var deferredMcp = deferredRows.SingleOrDefault(row => row.Key == "mcp-tools");
        Require(allDeferred.Categories.All(category => category.Name != "MCP tools") && deferredMcp is { Tokens: 0 } &&
            deferredRows.FindIndex(row => row.Key == "mcp-tools") == deferredRows.FindIndex(row => row.Key == "mcp-instructions") + 1 &&
            deferredMcp.Tip?.Contains("2.1k tokens", StringComparison.Ordinal) == true &&
            deferredMcp.Children.Select(server => server.Label).SequenceEqual(["docs", "notes"]) && deferredMcp.Children[0].Children.Count == 3 &&
            deferredMcp.Children.All(server => server.Suffix == " if loaded"),
            "With every MCP tool deferred the MCP tools row stays, at 0 loaded with the deferred total in its tip, listing its servers \"if loaded\" and their tools.");
        BaselineLineLayout();
        RowHoverShowsNothing();
        DeniedRow();
        ToolDocsLinks();
        CapabilityTree();
        PluginPartsTree();
        TreeConnectorBounds();
        Require(rows.Single(row => row.Key == "skills").Children.Select(skill => skill.Label).SequenceEqual(["dataviz", "example", "tidy"]),
            "Skills open into each skill and each plugin, heaviest first.");

        var view = new ClaudeContextUsageView();
        var window = new Window { Width = 520, Height = 200, Content = view };
        window.Show();
        try
        {
            var at = new DateTimeOffset(2026, 1, 1, 8, 50, 0, TimeSpan.Zero);
            view.Show(new(false) { Breakdown = Fixture(), MeasuredAt = at.ToString("O") });
            E2eWorkspace.Settle();
            Require(view.FindControl<TextBlock>("ClaudeContextMessage")!.Text == "Measured " + ClaudeParts.Ago(at) &&
                !view.FindControl<Button>("ClaudeContextRetry")!.IsVisible, "A measured baseline says only when it was measured.");
            view.Show(new(false) { Error = "boom" });
            Require(view.FindControl<Button>("ClaudeContextRetry")!.IsVisible &&
                view.FindControl<TextBlock>("ClaudeContextMessage")!.Text!.Contains("boom", StringComparison.Ordinal),
                "A failed measurement shows a small inline error with a retry.");
        }
        finally { window.Close(); }
        // Units: singular for one, and abbreviated where the row is narrower than the threshold.
        static string Drawn(long tokens, string amount, double width)
        {
            var token = new TokenAmount(amount, tokens, Avalonia.Media.Brushes.Gray, " in new session");
            var host = new Window { Width = width, Height = 60, Content = token };
            host.Show();
            try { E2eWorkspace.Settle(); return token.Text; }
            finally { host.Close(); }
        }
        Require(Drawn(1, "1", 480) == "1 token in new session" && Drawn(2_500, "2.5k", 480) == "2.5k tokens in new session" &&
            Drawn(1, "1", 200) == "1 tok in new session" && Drawn(2_500, "2.5k", 200) == "2.5k toks in new session",
            "Token amounts say 1 token, never 1 tokens, and abbreviate to tok/toks when narrow.");
        // The header label spells Context out where the row has room beside the token summary, CTX where it has not.
        static (string Label, string Summary, bool Trimmed, object? Tip, string? Name) Header(Window host, double width)
        {
            host.Width = width;
            E2eWorkspace.Settle();
            var label = host.GetLogicalDescendants().OfType<ClaudeContextLabel>().Single();
            var amount = host.GetLogicalDescendants().OfType<TokenAmount>().Single();
            return (label.Text, amount.Text, amount.Bounds.Width < amount.DesiredSize.Width, ToolTip.GetTip(label), Avalonia.Automation.AutomationProperties.GetName(label));
        }
        var heading = new DockPanel { Margin = new Thickness(8, 4) };
        var summary = new TokenAmount("22.5k", 22_500, Avalonia.Media.Brushes.Gray, " in new session");
        DockPanel.SetDock(summary, Avalonia.Controls.Dock.Right);
        heading.Children.Add(summary);
        heading.Children.Add(new ClaudeContextLabel());
        var headingHost = new Window { Width = 520, Height = 60, Content = heading };
        headingHost.Show();
        try
        {
            var wide = Header(headingHost, 520);
            var narrowed = Header(headingHost, 200);
            var widened = Header(headingHost, 520);
            Require(wide.Label == "CONTEXT" && widened.Label == "CONTEXT" && narrowed.Label == "CTX",
                $"The header says CONTEXT in a wide pane, CTX in a narrow one, and CONTEXT again once widened. Got {wide.Label}, {narrowed.Label}, {widened.Label}.");
            Require(!wide.Trimmed && !narrowed.Trimmed && !widened.Trimmed && wide.Summary == "22.5k tokens in new session",
                "The token summary is never trimmed, in either state.");
            Require(new[] { wide, narrowed, widened }.All(state => Equals(state.Tip, "Context") && state.Name == "Context"),
                "Both spellings keep the tooltip and automation name Context.");
        }
        finally { headingHost.Close(); }
        Console.WriteLine("PASS Claude Code context UI: baseline rows, MCP and skill parts, freshness note, inline error, token units");
    }

    // A long row name trims with an ellipsis before the chevron and the count; the chevron follows a name that fits,
    // and the count keeps its column at the right edge, in a narrow pane and a wide one.
    // No row (tool, skill, MCP server, plugin) shows anything on hover: no dots button, no bubble, no new text, no new height.
    // Its actions are in the right-click menu, which opens on the row and holds them.
    private static void RowHoverShowsNothing()
    {
        static IEnumerable<ClaudeConfigPanel.BaselineEntry> Flatten(IEnumerable<ClaudeConfigPanel.BaselineEntry> entries) =>
            entries.SelectMany(entry => new[] { entry }.Concat(Flatten(entry.Children)));
        var none = new HashSet<string>();
        var entries = Flatten(ClaudeConfigPanel.BaselineRows(Fixture(), claudePrompt: 1_300)).ToList();
        var edited = new List<PendingEdit>();
        var stack = new StackPanel();
        var rows = entries.Select(entry =>
        {
            var line = ClaudeConfigPanel.BaselineLine(entry, depth: 0, open: false);
            var actions = ClaudeConfigPanel.RowActions(entry, none, none);
            ClaudeConfigPanel.AttachRowActions(line, actions, edited.Add);
            stack.Children.Add(line);
            return (entry, line, actions);
        }).ToList();
        var host = new Window { Width = 520, Height = 900, Content = new ScrollViewer { Content = stack } };
        host.Show();
        try
        {
            E2eWorkspace.Settle();
            Require(rows.Any(row => row.actions.Count == 0) && rows.Any(row => row.actions.Count > 0) &&
                rows.Any(row => row.entry.Key.StartsWith("skills/", StringComparison.Ordinal) && row.actions.Count > 0) &&
                rows.Any(row => row.entry.Key.StartsWith("mcp-tools/", StringComparison.Ordinal) && row.actions.Count > 0) &&
                rows.Any(row => row.entry.Key.StartsWith("skills/", StringComparison.Ordinal) && row.entry.Tip?.StartsWith("Plugin", StringComparison.OrdinalIgnoreCase) == true && row.actions.Count > 0),
                "The fixture has rows with and without actions, with skills, MCP servers and plugins among them.");
            var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
            void Raise(Avalonia.Interactivity.RoutedEvent<Avalonia.Input.PointerEventArgs> routed, Control line) =>
                line.RaiseEvent(new Avalonia.Input.PointerEventArgs(routed, line, pointer, host, default, 0,
                    new Avalonia.Input.PointerPointProperties(), Avalonia.Input.KeyModifiers.None));
            double[] Heights() => [.. rows.Select(row => row.line.Bounds.Height)];
            var before = Heights();
            foreach (var (entry, line, actions) in rows)
            {
                Require(line.GetLogicalDescendants().OfType<Control>().All(node => node.Name is not ("ClaudeContextRowMore" or "ClaudeContextBubble" or "ClaudeContextRowToggle")),
                    $"Row {entry.Key} has no dots button, no hover bubble and no hover-only text.");
                string[] Texts() => [.. line.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "")];
                var textsBefore = Texts();
                var buttonsBefore = line.GetLogicalDescendants().OfType<Button>().Count();
                Raise(Avalonia.Input.InputElement.PointerEnteredEvent, line);
                E2eWorkspace.Settle();
                Require(Texts().SequenceEqual(textsBefore) && Heights().SequenceEqual(before) && line.GetLogicalDescendants().OfType<Button>().Count() == buttonsBefore &&
                    line.GetLogicalDescendants().OfType<Control>().All(node => node.Name is not ("ClaudeContextRowMore" or "ClaudeContextBubble")) &&
                    line.GetLogicalDescendants().OfType<Border>().All(border => !ReferenceEquals(border.Background, Tereditor.Plugins.UI.Kit.Ui.Hover)),
                    $"The pointer over {entry.Key} shows nothing new and changes no row height.");
                if (actions.Count > 0)
                {
                    Require(line.ContextMenu is not null, $"Row {entry.Key} has a right-click menu.");
                    line.ContextMenu!.Open(line);
                    E2eWorkspace.Settle();
                    Require(line.ContextMenu.IsOpen && line.ContextMenu.Items.OfType<MenuItem>().Select(item => (string?)item.Header).SequenceEqual(actions.Select(action => action.Label)) &&
                        Heights().SequenceEqual(before), $"The right-click menu of {entry.Key} holds its actions and changes no row height.");
                    line.ContextMenu.Close();
                    E2eWorkspace.Settle();
                }
                else Require(line.ContextMenu is null, $"Row {entry.Key} has no actions and no menu.");
                Raise(Avalonia.Input.InputElement.PointerExitedEvent, line);
                E2eWorkspace.Settle();
                Require(Texts().SequenceEqual(textsBefore) && Heights().SequenceEqual(before), $"Leaving {entry.Key} changes no text and no row height.");
            }
        }
        finally { host.Close(); }
    }

    // A built-in tool's row opens its docs page on a real left click; an unknown or MCP tool's row does nothing. Every mapped tool
    // links under the docs URL, and every built-in tool the Context table lists is mapped.
    private static void ToolDocsLinks()
    {
        Require(ClaudeToolDocs.Anchors.All(pair => ClaudeToolDocs.Url(pair.Key) is { } url && url.StartsWith(ClaudeToolDocs.DocsUrl, StringComparison.Ordinal) &&
            (pair.Value.Length == 0 || (url == ClaudeToolDocs.DocsUrl + "#" + pair.Value && pair.Value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))),
            "Every mapped tool has a docs URL under the tools reference, with a plain heading anchor when it has one.");
        Require(ClaudeConfigPanel.BuiltInTools.All(tool => ClaudeToolDocs.Url(tool) is not null) && ClaudeToolDocs.Url("mcp__x__y") is null,
            "Every built-in tool in the table is mapped, an unknown name is not.");
        var known = new ClaudeConfigPanel.BaselineEntry("system-tools/Edit", "Edit", "terminal", -1, []);
        var unknown = new ClaudeConfigPanel.BaselineEntry("system-tools/Nope", "Nope", "terminal", -1, []);
        Require(ClaudeSkillDocs.Urls.Values.All(url => url.StartsWith("https://code.claude.com/docs/", StringComparison.Ordinal)) && ClaudeSkillDocs.Url("dataviz") is not null &&
            ClaudeSkillDocs.Url("doctor") == ClaudeSkillDocs.SkillsUrl + "#check-your-setup-with-/doctor" && ClaudeSkillDocs.Url("example:review") is null && ClaudeSkillDocs.Url("mine") is null,
            "Every bundled skill maps under the docs, a plugin, user or unknown skill does not.");
        var skill = new ClaudeConfigPanel.BaselineEntry("skills/dataviz", "dataviz", "sparkling", 480, []);
        var fileSkill = new ClaudeConfigPanel.BaselineEntry("skills/debug", "debug", "sparkling", 20, []);
        var pluginSkill = new ClaudeConfigPanel.BaselineEntry("skills/example:review", "review", "sparkling", 20, []);
        var opened = new List<string>();
        var stack = new StackPanel();
        DockPanel Row(ClaudeConfigPanel.BaselineEntry entry, bool hasFile = false)
        {
            var line = ClaudeConfigPanel.BaselineLine(entry, 0, false);
            ClaudeConfigPanel.AttachToolLink(line, entry, false, opened.Add, hasFile);
            stack.Children.Add(line);
            return line;
        }
        var knownRow = Row(known);
        var unknownRow = Row(unknown);
        var skillRow = Row(skill);
        var fileSkillRow = Row(fileSkill, hasFile: true);
        var pluginSkillRow = Row(pluginSkill);
        var host = new Window { Width = 400, Height = 200, Content = stack };
        host.Show();
        try
        {
            E2eWorkspace.Settle();
            var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
            void Click(Control line) => line.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(line, pointer, host, default, 0,
                new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
                Avalonia.Input.KeyModifiers.None));
            Click(unknownRow);
            Require(opened.Count == 0, "A click on an unknown tool row opens nothing.");
            Click(fileSkillRow);
            Click(pluginSkillRow);
            Require(opened.Count == 0, "A click on a skill with a file, or a plugin skill, opens no docs.");
            Click(skillRow);
            Require(opened.SequenceEqual([ClaudeSkillDocs.ListUrl]), "A click on a bundled skill row asks to open its docs.");
            opened.Clear();
            Click(knownRow);
            Require(opened.SequenceEqual([ClaudeToolDocs.DocsUrl + "#edit-tool-behavior"]), "A click on the Edit row asks to open its docs section.");
        }
        finally { host.Close(); }
    }

    // A row turned off reads as off from its controls: struck-through dimmed name, dimmed icon, a Denied label where the
    // size or dash was, and the same height as when it is on.
    private static void DeniedRow()
    {
        var tool = ClaudeConfigPanel.BaselineRows(Fixture(), claudePrompt: 1_300).Single(row => row.Key == "system-tools").Children.Single(row => row.Label == "WebSearch");
        var skill = ClaudeConfigPanel.BaselineRows(Fixture(), claudePrompt: 1_300).Single(row => row.Key == "skills").Children.Single(row => row.Label == "dataviz");
        var none = new HashSet<string>();
        var stack = new StackPanel();
        Control Build(ClaudeConfigPanel.BaselineEntry entry, HashSet<string> denied, HashSet<string> skillsOff)
        {
            var actions = ClaudeConfigPanel.RowActions(entry, denied, skillsOff);
            var line = ClaudeConfigPanel.BaselineLine(entry, 0, false, ClaudeConfigPanel.IsOff(actions));
            stack.Children.Add(line);
            return line;
        }
        var allowed = Build(tool, none, none);
        var off = Build(tool, ["WebSearch"], none);
        var skillOff = Build(skill, none, ["dataviz"]);
        var host = new Window { Width = 400, Height = 200, Content = stack };
        host.Show();
        try
        {
            E2eWorkspace.Settle();
            TextBlock Name(Control line) => line.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == "ClaudeContextName");
            Require(Name(allowed).TextDecorations is null && !allowed.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Name == "ClaudeContextDenied"),
                "An allowed row has no strikethrough and no Denied label.");
            foreach (var line in new[] { off, skillOff })
            {
                var label = line.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == "ClaudeContextDenied");
                Require(Name(line).TextDecorations == Avalonia.Media.TextDecorations.Strikethrough && label.Text == "Denied" &&
                    !ReferenceEquals(Name(line).Foreground, Name(allowed).Foreground) && !line.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Text == "—"),
                    "A denied row strikes its name through in a dimmed tone and says Denied instead of the dash.");
            }
            Require(off.Bounds.Height == allowed.Bounds.Height, $"A denied row keeps its height ({off.Bounds.Height} against {allowed.Bounds.Height}).");
            var offLine = (DockPanel)off;
            ClaudeConfigPanel.AttachRowActions(offLine, ClaudeConfigPanel.RowActions(tool, new HashSet<string> { "WebSearch" }, none), _ => { });
            E2eWorkspace.Settle();
            Require(offLine.ContextMenu!.Items.OfType<MenuItem>().Select(item => (string?)item.Header).SequenceEqual(["Turn tool back on"]) &&
                offLine.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == "ClaudeContextDenied").Text == "Denied" && off.Bounds.Height == allowed.Bounds.Height,
                "A denied tool row offers Turn tool back on in its menu, keeps its Denied label and its height.");
        }
        finally { host.Close(); }
    }

    private static void BaselineLineLayout()
    {
        var tool = new ClaudeConfigPanel.BaselineEntry("mcp-tools/long/tool", "tool", "code", 300, []);
        var server = new ClaudeConfigPanel.BaselineEntry("mcp-tools/long", "plugin_tereditor_tereditor_with_a_long_name", "server", 11_100, [tool], Suffix: " if loaded");
        var line = ClaudeConfigPanel.BaselineLine(server, depth: 1, open: false);
        var host = new Window { Width = 280, Height = 60, Content = new StackPanel { Children = { line } } };
        host.Show();
        try
        {
            var name = line.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == "ClaudeContextName");
            var chevron = line.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(path => path.Name == "ClaudeContextChevron");
            var count = line.GetLogicalDescendants().OfType<TokenAmount>().Single();
            double Left(Visual visual) => visual.TranslatePoint(default, line)!.Value.X;
            foreach (var width in new[] { 280.0, 900 })
            {
                host.Width = width;
                E2eWorkspace.Settle();
                var nameRight = Left(name) + name.Bounds.Width;
                var trimmed = name.TextLayout.TextLines.Any(text => text.HasCollapsed);
                Require(nameRight <= Left(chevron) + 0.5 && Left(chevron) + chevron.Bounds.Width <= Left(count) + 0.5 &&
                    Math.Abs(Left(count) + count.Bounds.Width - line.Bounds.Width) < 1 && count.Bounds.Width >= count.DesiredSize.Width - 0.5,
                    $"At {width}px the name ends left of the chevron, the chevron left of the count, and the count keeps the right edge whole. " +
                    $"Name right {nameRight}, chevron {Left(chevron)}..{Left(chevron) + chevron.Bounds.Width}, count {Left(count)}, line {line.Bounds.Width}.");
                Require(width < 400 ? trimmed : !trimmed && Left(chevron) - nameRight < 8,
                    $"At {width}px a long name {(width < 400 ? "trims with an ellipsis" : "shows whole, the chevron right after it")}.");
            }
            Require(Equals(ToolTip.GetTip(name), server.Label), "A row's name has its full text in a tooltip.");
        }
        finally { host.Close(); }
    }

    private sealed class FakeCapabilityHost : IClaudeCapabilityHost
    {
        public HashSet<string> Collapsed { get; } = [];
        public List<string> Calls { get; } = [];
        public string? ProjectRoot { get; set; } = "/work/project";
        public void Open(string path, IReadOnlyList<string>? keyPath) => Calls.Add("open " + path);
        public void Review(PendingEdit pending) => Calls.Add("review");
        public void Uninstall(PluginUninstallTarget target) => Calls.Add("uninstall " + target.Name);
        public void Move(PluginMoveTarget target) => Calls.Add($"move {target.Name} {target.To.Name()}");
        public void MoveSkill(SkillMoveTarget target) => Calls.Add($"moveskill {target.Name} {target.To.Name()}");
        public void Marketplace(ClaudeMarketplaceAction action) => Calls.Add("marketplace");
        public void Add(string kind) => Calls.Add("add " + kind);
        public bool IsCollapsed(string key) => Collapsed.Contains(key);
        public void SetCollapsed(string key, bool collapsed) { if (collapsed) Collapsed.Add(key); else Collapsed.Remove(key); }
    }

    // Marketplaces are parent rows and the plugins installed from them hang below by the Instructions section's connectors;
    // plugins of no listed marketplace go under "No marketplace", so every plugin shows exactly once.
    private static void CapabilityTree()
    {
        static ClaudeCapability Market(string name, ClaudeConfigScope scope, string source) =>
            new(ClaudeCapabilityKind.Marketplace, name, new(scope, "/home/.claude/settings.json") { KeyPath = ["extraKnownMarketplaces", name] }, true) { Detail = source };
        static ClaudeCapability Plugin(string name, bool on = true) =>
            new(ClaudeCapabilityKind.Plugin, name, new(ClaudeConfigScope.Project, "/work/project/.claude/settings.json") { KeyPath = ["enabledPlugins", name] }, on)
            { Detail = name.Contains('@') ? name[(name.IndexOf('@') + 1)..] : null };
        IReadOnlyList<ClaudeCapability> capabilities =
        [
            Market("claude-code-plugins", ClaudeConfigScope.User, "anthropics/claude-code"), Market("empty-market", ClaudeConfigScope.Project, "/srv/market"),
            Plugin("ts-lsp@claude-code-plugins"), Plugin("tidy@claude-code-plugins", on: false), Plugin("gone@removed-market"), Plugin("inline"),
            new(ClaudeCapabilityKind.Skill, "ponytail", new(ClaudeConfigScope.User, "/home/.claude/skills/ponytail"), true)
        ];
        var groups = ClaudeCapabilitiesSurface.PluginGroups(capabilities);
        Require(groups.Select(group => group.Label).SequenceEqual(["claude-code-plugins", "empty-market", "No marketplace"]) &&
            groups[0].Plugins.Select(plugin => plugin.Name).SequenceEqual(["ts-lsp@claude-code-plugins", "tidy@claude-code-plugins"]) && groups[1].Plugins.Count == 0 &&
            groups[2].Marketplace is null && groups[2].Plugins.Select(plugin => plugin.Name).SequenceEqual(["gone@removed-market", "inline"]) &&
            ClaudeCapabilitiesSurface.PluginGroups(capabilities.Where(item => item.Kind != ClaudeCapabilityKind.Plugin).ToArray()).Select(group => group.Label).SequenceEqual(["claude-code-plugins", "empty-market"]),
            "Plugins group under their listed marketplace in listed order; the rest form a last group, which is absent when empty.");

        var host = new FakeCapabilityHost();
        var surface = new ClaudeCapabilitiesSurface(host, capabilities);
        var view = surface.View;
        var sections = view.GetLogicalDescendants().OfType<StackPanel>().Where(panel => panel.Name == "ClaudeCapabilitySection").ToArray();
        Require(sections.Select(section => section.Tag).SequenceEqual([ClaudeCapabilityKind.Plugin, ClaudeCapabilityKind.Skill]) &&
            sections[0].GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "PLUGINS"),
            "Plugins and marketplaces are one PLUGINS section, before Skills, with no separate marketplaces section.");
        var section = sections[0];
        var rows = section.GetLogicalDescendants().OfType<StackPanel>().Where(panel => panel.Name == "ClaudeCapability").Select(panel => ((ClaudeCapability)panel.Tag!).Name).ToArray();
        Require(rows.Length == 6 && rows.Distinct().Count() == 6 && capabilities.Where(item => item.Kind != ClaudeCapabilityKind.Skill).All(item => rows.Contains(item.Name)),
            "Every marketplace and plugin is drawn once. Got: " + string.Join(", ", rows));
        Require(section.GetLogicalDescendants().OfType<StackPanel>().Count(panel => panel.Name == "ClaudeCapabilityGroup") == 1 &&
            section.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "No marketplace"), "The group without a listed marketplace is a parent row named No marketplace.");

        // Order in the section: parent, its children, next parent.
        var order = section.Children.Skip(1).Select(child => child switch
        {
            StackPanel { Name: "ClaudeCapabilityChildren" } parts => "children:" + parts.Tag,
            _ => child.GetLogicalDescendants().OfType<StackPanel>().First(panel => panel.Name is "ClaudeCapability" or "ClaudeCapabilityGroup") is { Name: "ClaudeCapability" } row
                ? "row:" + ((ClaudeCapability)row.Tag!).Name : "group"
        }).ToArray();
        Require(order.SequenceEqual(["row:claude-code-plugins", "children:plugins/claude-code-plugins", "row:empty-market", "group", "children:plugins/\0none"]),
            "Parents come in listed order with their children right after, an empty marketplace has none, and No marketplace is last. Got: " + string.Join(", ", order));

        // The connectors are the Instructions section's: the same component and names, a tee then an elbow, hanging from the parent's stem.
        Control Children(string id) => section.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "ClaudeCapabilityChildren" && Equals(panel.Tag, id));
        string[] Connectors(Control parts) => [.. parts.GetLogicalDescendants().OfType<Grid>().Where(grid => grid.Name == "ClaudeContextConnector").Select(grid => (string)grid.Tag!)];
        Require(Connectors(Children("plugins/claude-code-plugins")).SequenceEqual(["tee", "last"]) && Connectors(Children("plugins/\0none")).SequenceEqual(["tee", "last"]) &&
            ClaudeTree.Guides(1, true).Single() is Grid { Name: "ClaudeContextConnector" } &&
            section.GetLogicalDescendants().OfType<Border>().Count(border => border.Name == "ClaudeContextStem") == 2,
            "Plugins hang from their marketplace by the shared tree connectors, and only parents with children carry a stem.");

        // Folding hides the children and the stem, is kept by the host, and does not change what is drawn.
        var key = "plugins/claude-code-plugins";
        Require(Children(key).IsVisible && !host.Collapsed.Contains(key), "Parents start open.");
        surface.Toggle(key);
        var stems = section.GetLogicalDescendants().OfType<Border>().Where(border => border.Name == "ClaudeContextStem").ToArray();
        Require(!Children(key).IsVisible && host.Collapsed.Contains(key) && !stems[0].IsVisible && stems[1].IsVisible && Children("plugins/\0none").IsVisible,
            "Folding a marketplace hides its plugins and its stem, and leaves the other group alone.");
        var folded = new FakeCapabilityHost { Collapsed = { key } };
        Require(!new ClaudeCapabilitiesSurface(folded, capabilities).View.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "ClaudeCapabilityChildren" && Equals(panel.Tag, key)).IsVisible,
            "A fold the pane remembers is drawn folded after a redraw.");
        surface.Toggle(key);
        Require(Children(key).IsVisible && stems[0].IsVisible && !host.Collapsed.Contains(key), "Unfolding shows them again.");

        // Under a listed marketplace the plugin does not repeat its marketplace; elsewhere the row still names it.
        var texts = section.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
        Require(!Children(key).GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "claude-code-plugins") &&
            Children("plugins/\0none").GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "removed-market") && texts.Contains("OFF"),
            "A plugin under its marketplace leaves out that name; a loose one keeps it; the OFF mark stays.");

        // Every row keeps its menu.
        string[] Names(ClaudeCapability item) => [.. surface.Actions(item).Select(action => action.Name)];
        Require(Names(capabilities[0]).SequenceEqual(["ClaudeMarketplaceUpdate", "ClaudeMarketplaceRemove"]) &&
            Names(capabilities[2]).SequenceEqual(["ClaudeCapabilityToggle", "ClaudeCapabilityMove_user", "ClaudeCapabilityMove_local", "ClaudeCapabilityUninstall"]) &&
            surface.Actions(capabilities[3]).First().Label == "Turn on" && surface.Actions(capabilities[2]).First().Label == "Turn off",
            "Marketplace rows offer update and remove; plugin rows offer switch, move and uninstall as before.");
        surface.Actions(capabilities[2])[3].Run();
        Require(host.Calls.SequenceEqual(["uninstall ts-lsp@claude-code-plugins"]), "A menu entry reaches the pane.");

        // A skill moves between the two folders it can live in: only the other one is offered, and "project" only with a project open.
        var userSkill = capabilities[6];
        var projectSkill = new ClaudeCapability(ClaudeCapabilityKind.Skill, "synced", new(ClaudeConfigScope.Project, "/work/project/.claude/skills/synced"), true);
        Require(Names(userSkill).SequenceEqual(["ClaudeCapabilityToggle", "ClaudeSkillMove_project"]) && Names(projectSkill).SequenceEqual(["ClaudeCapabilityToggle", "ClaudeSkillMove_user"]) &&
            surface.Actions(userSkill)[1].Label == "Move to project…" && surface.Actions(projectSkill)[1].Label == "Move to user…",
            "A user skill offers Move to project, a project skill Move to user, after Turn off.");
        host.ProjectRoot = null;
        Require(Names(userSkill).SequenceEqual(["ClaudeCapabilityToggle"]) && Names(projectSkill).SequenceEqual(["ClaudeCapabilityToggle", "ClaudeSkillMove_user"]),
            "With no project open, Move to project is not offered.");
        host.ProjectRoot = "/work/project";
        var managed = new ClaudeCapability(ClaudeCapabilityKind.Skill, "org-skill", new(ClaudeConfigScope.Managed), true);
        Require(Names(managed).SequenceEqual(["ClaudeCapabilityToggle"]), "A skill no folder holds (managed) cannot be moved.");
        host.Calls.Clear();
        surface.Actions(userSkill)[1].Run();
        surface.Actions(projectSkill)[1].Run();
        Require(host.Calls.SequenceEqual(["moveskill ponytail project", "moveskill synced user"]), "The move entries reach the pane with the target scope.");

        (ClaudeMcpStatus Status, string Word, IBrush Brush)[] states =
        [
            (ClaudeMcpStatus.Connected, "Connected", Tereditor.Plugins.UI.Kit.Ui.Success), (ClaudeMcpStatus.NeedsAuth, "Needs sign-in", Tereditor.Plugins.UI.Kit.Ui.Warning), (ClaudeMcpStatus.Pending, "Starting", Tereditor.Plugins.UI.Kit.Ui.Warning),
            (ClaudeMcpStatus.Failed, "Failed", Tereditor.Plugins.UI.Kit.Ui.Danger), (ClaudeMcpStatus.Disabled, "Disabled", Tereditor.Plugins.UI.Kit.Ui.Muted), (ClaudeMcpStatus.Unknown, "? odd", Tereditor.Plugins.UI.Kit.Ui.Muted)
        ];
        var servers = states.Select(state => new ClaudeCapability(ClaudeCapabilityKind.Mcp, "claude.ai " + state.Status, new(ClaudeConfigScope.User), true)
        { Detail = "https://api.example.com/mcp", Status = state.Status, StatusText = "? odd" }).ToArray();
        var badges = new ClaudeCapabilitiesSurface(host, servers).View.GetLogicalDescendants().OfType<TextBlock>().Where(block => block.Name == "ClaudeMcpStatus").ToArray();
        Require(badges.Length == states.Length && badges.Zip(states).All(pair => pair.First.Text == pair.Second.Word && ReferenceEquals(pair.First.Foreground, pair.Second.Brush)),
            "A server row shows its status as a badge worded and colored by state; an unknown status shows Claude's text, muted.");
        var localServer = new ClaudeCapability(ClaudeCapabilityKind.Mcp, "scratch", new(ClaudeConfigScope.Local, "/home/.claude.json") { KeyPath = ["projects", "/p", "mcpServers", "scratch"] }, true);
        var localRow = new ClaudeCapabilitiesSurface(host, [localServer]).View;
        var localChips = localRow.GetLogicalDescendants().OfType<Border>().Where(border => border.Name == "ClaudeScopeChip").ToArray();
        Require(localChips.Length == 1 && (localChips[0].Child as TextBlock)?.Text == "LOCAL \u00B7 this project only" &&
            !localRow.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Text == "this project only"),
            "A local server row has one scope chip carrying the scope and its meaning, and no separate meaning line.");
        Require(McpList.Capabilities(McpList.Parse("claude.ai Gmail: https://gmail.mcp.claude.com/mcp - ✔ Connected\n"), new HashSet<string>(), "/p").Single() is { Status: ClaudeMcpStatus.Connected, Detail: "https://gmail.mcp.claude.com/mcp" },
            "The detail line keeps only the target; the status travels apart.");
        Console.WriteLine("PASS Claude Code capabilities: marketplaces and their plugins form one tree with the Instructions connectors, fold, and keep their menus");
    }

    // The tree connectors of a group stay inside it: the vertical line of the first level ends at the elbow of the group's last plugin and
    // never reaches the row's bottom edge, the next group or the pane's left edge, with one child or several, one line of text or wrapped.
    private static void TreeConnectorBounds()
    {
        const string longText = "Does a great many things for the project, and the line that says so is long enough to wrap in a narrow pane several times over";
        ClaudePluginPart[] parts =
        [
            new(ClaudePluginPartKind.Skill, "tools:alpha") { Description = longText, Path = "/cache/tools/skills/alpha/SKILL.md" },
            new(ClaudePluginPartKind.Skill, "tools:beta") { Description = longText }, new(ClaudePluginPartKind.Agent, "tools:helper") { Description = "Helps" }
        ];
        ClaudeCapability Plugin(string name, string market, IReadOnlyList<ClaudePluginPart>? contents) =>
            new(ClaudeCapabilityKind.Plugin, name, new(ClaudeConfigScope.User, "/home/.claude/settings.json") { KeyPath = ["enabledPlugins", name] }, true) { Detail = market, Parts = contents };
        ClaudeCapability Market(string name) => new(ClaudeCapabilityKind.Marketplace, name, new(ClaudeConfigScope.User, "/home/.claude/settings.json"), true) { Detail = "acme/" + name };
        IReadOnlyList<ClaudeCapability> capabilities =
        [
            Market("one"), Plugin("solo@one", "one", [parts[2]]),
            Market("several"), Plugin("first@several", "several", parts), Plugin("second@several", "several", null), Plugin("third@several", "several", [parts[0]]),
            Plugin("loose@" + longText, longText, parts)
        ];
        foreach (var width in new[] { 260.0, 700 })
            foreach (var opened in new[] { false, true })
            {
                var surface = new ClaudeCapabilitiesSurface(new FakeCapabilityHost(), capabilities);
                var view = surface.View;
                var window = new Window { Width = width, Height = 3000, Content = view };
                window.Show();
                try
                {
                    if (opened) foreach (var plugin in capabilities.Where(item => item.Parts is { Count: > 0 })) surface.Toggle("plugin/" + plugin.Name);
                    E2eWorkspace.Settle();
                    var section = view.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "ClaudeCapabilitySection");
                    var groups = section.Children.OfType<StackPanel>().Where(panel => panel.Name == "ClaudeCapabilityChildren").ToArray();
                    Require(groups.Length == 3 && groups[0].Bounds.Height > 0, $"Each marketplace with plugins and the group without one has a laid out children panel: window {window.Bounds}, view {view.Bounds}, groups {string.Join("; ", groups.Select(g => g.Bounds))}.");
                    foreach (var group in groups)
                    {
                        string where = $"(width {width}, {(opened ? "parts open" : "parts folded")}, {group.Tag})";
                        Rect Of(Control control) => new(control.TranslatePoint(default, group)!.Value, control.Bounds.Size);
                        var guides = group.GetVisualDescendants().OfType<Grid>().Where(grid => grid.IsEffectivelyVisible && grid.Margin.Left == 15 && grid.Width == 5).ToArray();
                        static string guide0(Border arm) => $"{((Control)arm.Parent!).Bounds} visible {arm.IsVisible}/{arm.IsEffectivelyVisible}";
                        var plugins = group.Children.OfType<DockPanel>().ToArray();
                        var elbow = Of(plugins[^1].Children.OfType<Grid>().Single(grid => grid.Name == "ClaudeContextConnector")).Center.Y;
                        foreach (var rule in guides.SelectMany(guide => guide.Children.OfType<Border>()).Where(border => border.Width == 1))
                        {
                            var rect = Of(rule);
                            Require(rect.Left >= -0.5 && rect.Top >= -0.5 && rect.Bottom <= group.Bounds.Height - 1 + 0.5,
                                $"A vertical tree line stays inside its group {where}: {rect} in {group.Bounds.Size}.");
                            if (rect.Left < 20) Require(rect.Bottom <= elbow + 0.5, $"The first-level line ends at the elbow of the last plugin {where}: line {rect}, elbow {elbow}.");
                        }
                        foreach (var arm in guides.SelectMany(guide => guide.Children.OfType<Border>()).Where(border => border.Height == 1))
                            Require(Of(arm).Left >= 14.5, $"An elbow starts at its line {where}: arm {Of(arm)} {arm.Bounds}, guide {guide0(arm)}, row {plugins[0].Bounds}, group {group.Bounds}.");
                        foreach (var row in plugins.Concat(group.GetVisualDescendants().OfType<DockPanel>().Where(p => p.IsEffectivelyVisible && p.Children.OfType<Grid>().Any(g => g.Margin.Left == 15 && g.Width == 5))).Distinct())
                            foreach (var rule in row.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Border>()).Where(b => b.Width == 1))
                                Require(rule.TranslatePoint(new(0, rule.Bounds.Height), row)!.Value.Y <= row.Bounds.Height - 1 + 0.5, $"A tree line stops above the row's bottom edge {where}: {Of(rule)} in row {row.Bounds}.");
                        var parent = section.Children[section.Children.IndexOf(group) - 1];
                        var stem = parent.GetVisualDescendants().OfType<Border>().SingleOrDefault(border => border.Name == "ClaudeContextStem");
                        Require(stem is null || stem.TranslatePoint(new(0, stem.Bounds.Height), parent)!.Value.Y <= parent.Bounds.Height + parent.Margin.Bottom + 0.5,
                            $"A parent's stem ends at the bottom of its row {where}.");
                        // The tree draws no border of its own: no bordered element of this group or its parent row meets a connector or a stem.
                        Rect InSection(Control control) => new(control.TranslatePoint(default, section)!.Value, control.Bounds.Size);
                        var strokes = guides.SelectMany(guide => guide.Children.OfType<Border>()).Select(line => InSection(line))
                            .Concat(section.GetVisualDescendants().OfType<Border>().Where(border => border.Name == "ClaudeContextStem").Select(stemBorder => InSection(stemBorder))).ToArray();
                        var edges = group.GetVisualDescendants().OfType<Border>().Concat(parent.GetVisualDescendants().OfType<Border>())
                            .Where(border => border.BorderThickness != default && border.IsEffectivelyVisible).Select(border => InSection(border)).ToArray();
                        foreach (var edge in edges)
                            foreach (var line in strokes)
                                Require(!edge.Intersects(line), $"A bordered element never meets a tree line {where}: edge {edge}, line {line}.");
                    }
                }
                finally { window.Close(); }
            }
        Console.WriteLine("PASS Claude Code plugin tree: connector lines stay inside their group for one, several and wrapped children");
    }

    // A plugin row says what it adds in a count line and, opened, lists the parts by kind under it, each opening its file.
    private static void PluginPartsTree()
    {
        ClaudePluginPart[] parts =
        [
            new(ClaudePluginPartKind.Skill, "tools:alpha") { Description = "Does alpha", Path = "/cache/tools/skills/alpha/SKILL.md" },
            new(ClaudePluginPartKind.Skill, "tools:beta"), new(ClaudePluginPartKind.Agent, "tools:helper") { Description = "Helps", Path = "/cache/tools/agents/helper.md" },
            new(ClaudePluginPartKind.Mcp, "plugin:tools:gh") { Description = "http", Path = "/cache/tools/.mcp.json" }
        ];
        ClaudeCapability Plugin(string name, IReadOnlyList<ClaudePluginPart>? contents, bool on = true) =>
            new(ClaudeCapabilityKind.Plugin, name, new(ClaudeConfigScope.User, "/home/.claude/settings.json") { KeyPath = ["enabledPlugins", name] }, on) { Detail = "market", Parts = contents };
        IReadOnlyList<ClaudeCapability> capabilities =
        [
            new(ClaudeCapabilityKind.Marketplace, "market", new(ClaudeConfigScope.User, "/home/.claude/settings.json"), true) { Detail = "acme/market" },
            Plugin("tools@market", parts), Plugin("off@market", [parts[0]], on: false), Plugin("gone@market", null), Plugin("bare@market", [])
        ];
        var host = new FakeCapabilityHost();
        var surface = new ClaudeCapabilitiesSurface(host, capabilities);
        var section = surface.View.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "ClaudeCapabilitySection");
        string[] Summaries() => [.. section.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.Name is "ClaudePluginSummary" or "ClaudePluginMissing").Select(text => text.Text!)];
        Require(Summaries().SequenceEqual(["2 skills, 1 agent, 1 MCP server", "1 skill", "Files not found", "Adds nothing"]),
            "Each plugin row counts what it adds, and says so when its files are not found: " + string.Join(" | ", Summaries()));
        var key = "plugin/tools@market";
        StackPanel Panel(string id) => section.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "ClaudeCapabilityChildren" && Equals(panel.Tag, id));
        Require(!Panel(key).IsVisible && !Panel("plugin/off@market").IsVisible && !section.GetLogicalDescendants().OfType<StackPanel>().Any(panel => panel.Tag?.Equals("plugin/gone@market") == true),
            "A plugin's parts start folded, and a plugin with nothing to list has no panel.");
        surface.Toggle(key);
        Require(Panel(key).IsVisible && host.Collapsed.Contains(key), "A click on the plugin opens its parts and the pane remembers it.");
        string[] Headings() => [.. Panel(key).GetLogicalDescendants().OfType<TextBlock>().Where(text => text.Name == "ClaudePluginKind").Select(text => text.Text!)];
        string[] Names() => [.. Panel(key).GetLogicalDescendants().OfType<StackPanel>().Where(panel => panel.Name == "ClaudePluginPart").Select(panel => ((ClaudePluginPart)panel.Tag!).Name)];
        Require(Headings().SequenceEqual(["Skills · 2", "Agents · 1", "MCP servers · 1"]) && Names().SequenceEqual(["tools:alpha", "tools:beta", "tools:helper", "plugin:tools:gh"]),
            "The parts are grouped by kind in a fixed order, with their names as Claude Code shows them.");
        Require(Panel(key).GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Does alpha") &&
            Panel(key).GetLogicalDescendants().OfType<Button>().Count(button => button.Name == "ClaudeOpenSource") == 3, "A part shows its description and a source button where it has a file.");
        var connectors = Panel(key).GetLogicalDescendants().OfType<Grid>().Where(grid => grid.Name == "ClaudeContextConnector").Select(grid => (string)grid.Tag!).ToArray();
        Require(connectors.SequenceEqual(["tee", "tee", "last", "tee", "last", "last", "last"]),
            "Headings and parts hang from the plugin by the shared connectors: " + string.Join(",", connectors));
        surface.Toggle("plugin/off@market");
        Require(Math.Abs(Panel("plugin/off@market").Opacity - 0.55) < 0.01 && Panel(key).Opacity == 1, "A disabled plugin's parts are drawn greyed.");
        var reopened = new ClaudeCapabilitiesSurface(host, capabilities).View.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "ClaudeCapabilityChildren" && Equals(panel.Tag, key));
        Require(reopened.IsVisible, "An open plugin is drawn open after a redraw.");
        Console.WriteLine("PASS Claude Code plugin tree: a plugin row counts what it adds and opens into its parts by kind, greyed when off");
    }
}