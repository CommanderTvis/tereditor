using System.Diagnostics;
using System.Reflection;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.AirGateway.UI;
using Tereditor.Plugins.Codex;
using Tereditor.Plugins.Providers.UI;

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
        Require(LauncherAvailability.Of([Wrap("w")], null) == new LauncherAvailability(true) && LauncherAvailability.Of([blocked], "claude was not found on PATH.") == new LauncherAvailability(false, "Sign in first.", false) &&
            LauncherAvailability.Of([], "claude was not found on PATH.") == new LauncherAvailability(false, "claude was not found on PATH.", false) &&
            LauncherAvailability.Of([], null, true) == new LauncherAvailability(true, null, true) && LauncherAvailability.Of([blocked], null, true) == new LauncherAvailability(false, "Sign in first.", true), "a launcher is unavailable for a blocked contribution first, then for a missing program");

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
        Roster();
        Console.WriteLine("PASS agent roster: contents for plugin states and fake PATH probes (installed, not installed, undecided, blocked), the change notification, and the lists that hide or keep entries");
    }

    private static PluginRosterEntry Plugin(string id, PluginStatus status) => new(id, id, "terminal", "1", 1, PluginOrigin.Builtin, status);

    private static void Roster()
    {
        var root = Path.Combine(Path.GetTempPath(), "tereditor-roster-" + Guid.NewGuid().ToString("N"));
        var empty = Path.Combine(root, "empty");
        var found = Path.Combine(root, "found");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(found);
        try
        {
            File.WriteAllText(Path.Combine(found, "claude"), "");
            var blocked = new AgentLaunchContribution { BlockedReason = "Sign in first." };
            AgentLauncher Launcher(string id, string label, string command, string path, string product, params AgentLaunchContribution[] contributions) =>
                new(id, label, "terminal", _ => command, () =>
                {
                    var install = AgentExecutable.Probe(command, path, false);
                    return LauncherAvailability.Of(contributions, install.Reason(product), install.Installed);
                });
            var launchers = new[]
            {
                Launcher("claude", "Claude Code", "claude", found, "Claude Code"),
                Launcher("codex", "Codex", "codex", empty, "Codex"),
                Launcher("pi", "Pi", "npx pi", empty, "Pi"),
                Launcher("acp-x", "Agent X", "agent-x", found, "Agent X")
            };
            var plugins = new[] { Plugin("claude-code", PluginStatus.Active), Plugin("codex", PluginStatus.Active), Plugin("omp", PluginStatus.Disabled), Plugin("pi", PluginStatus.Failed) };
            var roster = AgentRoster.Build(launchers, plugins);
            Require(roster.Select(entry => entry.Id).SequenceEqual(["claude-code", "codex", "omp", "pi", "acp-x"]), "the built-in agents come first, then other launchers: " + string.Join(",", roster.Select(entry => entry.Id)));
            Require(roster[0] is { LauncherId: "claude", Name: "Claude Code", PluginId: "claude-code", PluginState: AgentPluginState.Active, Installed: true, Reason: null, Offered: true }, "an agent found on the PATH is installed and offered");
            Require(roster[1] is { PluginState: AgentPluginState.Active, Installed: false, Offered: false } && roster[1].Reason == "codex was not found on PATH. Install Codex, then try again.", "an agent not found has the plain reason and is not offered");
            Require(roster[2] is { PluginId: "omp", PluginState: AgentPluginState.Disabled, Installed: null, Reason: null, Offered: false, PluginOn: false }, "an agent whose plugin is off is listed, not offered, and not probed");
            Require(roster[3] is { PluginState: AgentPluginState.Failed, Installed: null, Offered: true }, "an undecided probe counts as offered, and a failed plugin counts as on");
            Require(roster[4] is { PluginId: null, PluginState: AgentPluginState.Active, Installed: true }, "another plugin's launcher is listed with its probe");
            Require(AgentRoster.Build([], [Plugin("pi", PluginStatus.Refused)]).Select(entry => entry.PluginState).SequenceEqual([AgentPluginState.Absent, AgentPluginState.Absent, AgentPluginState.Absent, AgentPluginState.Refused]),
                "a plugin the host does not know is absent, and a refused one is refused");
            var gated = AgentRoster.Build([Launcher("claude", "Claude Code", "claude", found, "Claude Code", blocked), Launcher("codex", "Codex", "codex", empty, "Codex", blocked)], plugins);
            Require(gated[0] is { Installed: true, Reason: "Sign in first.", Offered: true } && gated[1] is { Installed: false, Reason: "Sign in first.", Offered: false }, "a blocked contribution's reason is the entry's reason; installed stays what the probe found");
            var broken = AgentRoster.Build([new("claude", "Claude Code", "terminal", _ => "claude", () => throw new InvalidOperationException("probe broke"))], plugins);
            Require(broken[0] is { Reason: "probe broke", Installed: null, Offered: true }, "a launcher that throws is listed with the error and does not break the roster");

            // The change notification, over a fake context: a launcher's probe answer, the plugin states, no change, and a stop.
            var path = empty;
            var context = Context.Create(
                [new("claude", "Claude Code", "terminal", _ => "claude", () => { var install = AgentExecutable.Probe("claude", path, false); return LauncherAvailability.Of([], install.Reason("Claude Code"), install.Installed); })],
                [Plugin("claude-code", PluginStatus.Active)]);
            var seen = new List<IReadOnlyList<AgentRosterEntry>>();
            using (var watch = AgentRoster.Watch(context, seen.Add))
            {
                Require(AgentRoster.Read(context)[0].Installed == false, "the roster reads the launcher's cached answer");
                Context.Raise(launchers: true);
                Require(seen.Count == 0, "no change means no call");
                path = found;
                Context.Raise(launchers: true);
                Require(seen.Count == 1 && seen[0][0].Installed == true && seen[0][0].Offered, "a new probe answer calls the handler with the new roster");
                Context.Plugins = [Plugin("claude-code", PluginStatus.Disabled)];
                Context.Raise(host: true);
                Require(seen.Count == 2 && seen[1][0] is { PluginState: AgentPluginState.Disabled, Installed: null }, "a plugin state change calls the handler");
                Context.Raise(launchers: true, host: true);
                Require(seen.Count == 2, "an unchanged roster is not announced twice");
            }
            Context.Plugins = [Plugin("claude-code", PluginStatus.Active)];
            Context.Raise(launchers: true, host: true);
            Require(seen.Count == 2, "a stopped observation is not called");
        }
        finally { Directory.Delete(root, true); }

        // The lists that consume the roster.
        AgentRosterEntry Entry(string id, AgentPluginState state, bool? installed, string? reason = null) => new(id, id, id, id, state, installed, reason);
        var installed = Entry("codex", AgentPluginState.Active, true);
        var missing = Entry("codex", AgentPluginState.Active, false, "Codex was not found on PATH. Install Codex, then try again.");
        Require(AirSettingsView.AgentRow("Codex", "Codex", false, installed) == (true, "Codex", false) && AirSettingsView.AgentRow("Codex", "Codex", false, Entry("codex", AgentPluginState.Active, null)) == (true, "Codex", false),
            "Air Gateway shows an installed or undecided agent");
        Require(AirSettingsView.AgentRow("Codex", "Codex", false, missing).Show == false && AirSettingsView.AgentRow("Codex", "Codex", true, missing) == (true, missing.Reason!, true),
            "Air Gateway hides an agent that is not installed, and keeps a chosen one as a dimmed line with the reason");
        Require(ProvidersView.Offered([Entry("pi", AgentPluginState.Active, true), Entry("omp", AgentPluginState.Active, false)]) == (true, false) && ProvidersView.Offered([Entry("pi", AgentPluginState.Active, null), Entry("omp", AgentPluginState.Active, null)]) == (true, true),
            "Model providers hide an agent that is not installed, and show an undecided one");
    }

    /// <summary>A fake plugin UI context: launchers, a plugin roster and the two observations the agent roster uses.</summary>
    public class Context : DispatchProxy
    {
        private static IReadOnlyList<AgentLauncher> launchers = [];
        private static readonly List<Action<IReadOnlyList<AgentLauncher>>> launcherHandlers = [];
        private static readonly List<Action> hostChecks = [];

        internal static IReadOnlyList<PluginRosterEntry> Plugins { get; set; } = [];

        internal static IPluginUIContext Create(IReadOnlyList<AgentLauncher> registered, IReadOnlyList<PluginRosterEntry> roster)
        {
            (launchers, Plugins) = (registered, roster);
            launcherHandlers.Clear();
            hostChecks.Clear();
            return Create<IPluginUIContext, Context>();
        }

        internal static void Raise(bool launchers = false, bool host = false)
        {
            if (launchers) foreach (var handler in launcherHandlers.ToArray()) handler(Context.launchers);
            if (host) foreach (var check in hostChecks.ToArray()) check();
        }

        private static PluginHostProjection Projection() => new([], new Dictionary<string, IReadOnlyList<HostWorkspace>>(), null, null, null,
            new PluginAppSettings("t", false, 80, false, 80, false, []), new Dictionary<string, IReadOnlyList<TerminalTabInfo>>(), new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, int>(), Plugins, null);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "Launchers": return launchers;
                case "Host": return Projection();
                case "OnLaunchersChanged":
                {
                    var handler = (Action<IReadOnlyList<AgentLauncher>>)args![0]!;
                    launcherHandlers.Add(handler);
                    return new Stop(() => launcherHandlers.Remove(handler));
                }
                case "WatchHost":
                {
                    var selector = (Delegate)args![0]!;
                    var listener = (Delegate)args[1]!;
                    var last = selector.DynamicInvoke(Projection());
                    void Check()
                    {
                        var next = selector.DynamicInvoke(Projection());
                        if (Equals(next, last)) return;
                        var before = last;
                        last = next;
                        listener.DynamicInvoke(next, before);
                    }
                    hostChecks.Add(Check);
                    return new Stop(() => hostChecks.Remove(Check));
                }
                default: throw new NotSupportedException(method.Name);
            }
        }

        private sealed class Stop(Action stop) : IDisposable
        {
            public void Dispose() => stop();
        }
    }
}