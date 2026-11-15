using System.Diagnostics;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Codex;

namespace Tereditor.Checks;

// The agent launch seam in isolation: ordering, replacement, quoting and the custom-command guard of
// AgentLaunchComposition, and its use by the Codex launch line.
internal static class AgentLaunchContributionChecks
{
    private static void Equal(string actual, string expected, string message)
    {
        if (actual != expected) throw new InvalidOperationException($"{message}: expected [{expected}] but got [{actual}]");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static AgentLaunchContribution Wrap(params string[] words) => new() { Wrapper = words };
    private static AgentLaunchContribution Replace(params string[] words) => new() { Wrapper = words, ReplacesExecutable = true };

    internal static void Run()
    {
        Equal(AgentLaunchComposition.Apply("claude --model opus", "claude", [], false), "claude --model opus", "no contributions leaves the line alone");
        Equal(AgentLaunchComposition.Apply("claude --model opus", "claude", [Replace("central", "run", "claude-code", "--")]), "central run claude-code -- --model opus", "a replacer stands in for the executable");
        Equal(AgentLaunchComposition.Apply("/usr/bin/claude -x", "claude", [Wrap("time")]), "time /usr/bin/claude -x", "a wrapper prefixes the executable and keeps its path");
        Equal(AgentLaunchComposition.Apply("'/My Apps/claude' -x", "claude", [Replace("r", "--")]), "r -- -x", "a quoted executable path is recognised");
        Equal(AgentLaunchComposition.Apply("npx claude", "claude", [Replace("r", "--")]), "npx claude", "a custom command line is never rewritten");
        Equal(AgentLaunchComposition.Apply("claude.exe a", "claude", [Wrap("w")], true), "w claude.exe a", "a .exe suffix still names the agent");

        // Ordering: non-replacing wrappers outermost first, then the first replacer, then arguments in order, first variable wins.
        var first = new AgentLaunchContribution { Wrapper = ["outer"], Arguments = ["--a"], Environment = new Dictionary<string, string> { ["X"] = "1", ["TEREDITOR_X"] = "no", ["bad name"] = "no" } };
        var second = new AgentLaunchContribution { Wrapper = ["inner"], Arguments = ["--b"], Environment = new Dictionary<string, string> { ["X"] = "2", ["Y"] = "two words" } };
        Equal(AgentLaunchComposition.Apply("codex go", "codex", [first, Replace("run", "--"), second, Replace("ignored", "--")]),
            "X=1 Y='two words' outer inner run -- --a --b go", "wrappers, then the first replacer, then arguments; first variable wins; reserved names dropped");

        // Quoting: words are literal on POSIX, and unsafe words on Windows skip the contribution.
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = ["a b", "it's", "$(x);`y`"], Arguments = ["--p", "a\nb"] }]), "claude", "a control character skips the contribution");
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = ["a b", "it's", "$(x);`y`"] }]), "'a b' 'it'\\''s' '$(x);`y`' claude", "POSIX words are single-quoted");
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = ["C:\\Program Files\\c.exe"], Environment = new Dictionary<string, string> { ["X"] = "1" } }], true), "\"C:\\Program Files\\c.exe\" claude", "Windows words are double-quoted and variables dropped");
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = ["a%PATH%"] }], true), "claude", "a Windows word with expansion characters skips the contribution");
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = [""] }]), "claude", "an empty word skips the contribution");

        // The Codex line applies contributions to its executable only, under its own prefixes.
        Equal(CodexLaunch.Line("codex", new(false, false) { Resume = true, ResumeSessionId = "abc", Contributions = [Replace("central", "run", "codex", "--")] }), "central run codex -- resume abc", "the Codex line routes through a replacer");
        Equal(CodexLaunch.Line("codex", new(false, false) { UiLaunch = true, Contributions = [Wrap("w")] }), "TEREDITOR_UI_LAUNCH=codex w codex", "tereditor's own prefix stays outermost");
        Equal(CodexLaunch.Line("my-codex --x", new(false, false) { Contributions = [Replace("central", "run", "codex", "--")] }), "my-codex --x", "a custom Codex command is not wrapped");
        // A blocked contribution is an unavailable launcher with its reason, and any line that still reaches it fails closed.
        var blocked = new AgentLaunchContribution { BlockedReason = "Sign in first." };
        Equal(AgentLaunchComposition.Blocked([Wrap("w"), blocked]) ?? "", "Sign in first.", "the first blocked reason is reported");
        Require(AgentLaunchComposition.Blocked([Wrap("w"), new() { BlockedReason = " " }]) is null, "a blank reason does not block");
        var line = AgentLaunchComposition.Apply("claude --model opus", "claude", [Wrap("w"), blocked]);
        Equal(line, "sh -c 'echo '\\''launcher unavailable: Sign in first.'\\'' >&2; exit 1'", "a blocked launch prints the reason and exits 1");
        Equal(AgentLaunchComposition.Apply("claude", "claude", [blocked], true), AgentLaunchComposition.BlockedWindowsCommand, "a blocked launch on Windows runs a program that does not exist");
        Equal(AgentLaunchComposition.Apply("npx claude", "claude", [blocked]), "npx claude", "a custom command line is still left alone");
        Equal(CodexLaunch.Line("codex", new(false, false) { Contributions = [blocked] }).Split(' ')[0], "sh", "the Codex line fails closed too");
        if (!OperatingSystem.IsWindows())
        {
            using var shell = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", AgentLaunchComposition.Apply("claude", "claude", [new() { BlockedReason = "it's 'quoted' $(x)" }])]) { RedirectStandardError = true, UseShellExecute = false })!;
            var message = shell.StandardError.ReadToEnd().Trim();
            shell.WaitForExit();
            Require(shell.ExitCode == 1 && message == "launcher unavailable: it's 'quoted' $(x)", "the printed reason is literal text: " + message);
        }
        Require(LauncherAvailability.Of([Wrap("w")], null) == new LauncherAvailability(true) && LauncherAvailability.Of([blocked], "claude was not found on PATH.") == new LauncherAvailability(false, "Sign in first.") &&
            LauncherAvailability.Of([], "claude was not found on PATH.") == new LauncherAvailability(false, "claude was not found on PATH."), "a launcher is unavailable for a blocked contribution first, then for a missing program");

        // The installed probe: found, not found and undecided, over a fake PATH in a temp directory.
        var root = Path.Combine(Path.GetTempPath(), "tereditor-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "claude"), "");
            Require(AgentExecutable.Probe("claude", root + Path.PathSeparator + "relative", false) == new AgentInstall(true, "claude"), "a program on the PATH is found");
            Require(AgentExecutable.Probe("codex", root, false) == new AgentInstall(false, "codex") && AgentExecutable.Probe("codex", root, false).Reason("Codex") == "codex was not found on PATH. Install Codex, then try again.", "a missing program says so in a plain sentence");
            Require(AgentExecutable.Probe(Path.Combine(root, "claude"), "", false).Installed == true && AgentExecutable.Probe(Path.Combine(root, "nope"), "", false).Installed == false, "a command with a directory is checked where it says");
            Require(AgentExecutable.Probe("claude", "", false).Installed is null, "an empty PATH is undecided");
            Require(AgentExecutable.Probe("npx claude", root, false).Installed is null && AgentExecutable.Probe("~/bin/claude", root, false).Installed is null && AgentExecutable.Probe("", root, false).Installed is null && AgentExecutable.Probe("claude", root, false).Reason("Claude Code") is null,
                "a command line the probe cannot read is undecided, and an undecided or found program has no reason");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS agent launch contributions: ordering, replacement, quoting and the custom-command guard");
    }
}