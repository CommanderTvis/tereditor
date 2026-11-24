using System.Reflection;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Input;

using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.BranchGraph;
using Tereditor.Plugins.BranchGraph.UI;
using Tereditor.Plugins.GitHub.Host;

namespace Tereditor.Checks;

/// <summary>The Git Graph's CI status: GitHub's batched lookup against canned gh output, and the row's icon and tooltip. Nothing reaches a network.</summary>
internal static partial class CommitChecksChecks
{
    private const string Remote = "https://github.com/o/r.git";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Sha(int number) => number.ToString("x").PadLeft(40, '0');

    private static string Counts(string group, params (string State, int Count)[] counts) =>
        "\"" + group + "\":[" + string.Join(',', counts.Select(item => $"{{\"state\":\"{item.State}\",\"count\":{item.Count}}}")) + "]";

    private static string Node(int success, int failed, int running, string nodes, int cancelled = 0) =>
        "{\"statusCheckRollup\":{\"contexts\":{\"totalCount\":" + (success + failed + running + cancelled) + "," +
        Counts("checkRunCountsByState", ("SUCCESS", success), ("FAILURE", failed), ("IN_PROGRESS", running), ("CANCELLED", cancelled)) + "," +
        Counts("statusContextCountsByState", ("SUCCESS", 0), ("PENDING", 0)) + ",\"nodes\":[" + nodes + "]}}}";

    private static string Run(string name, string conclusion, string status = "COMPLETED", long workflow = 0) =>
        $"{{\"__typename\":\"CheckRun\",\"name\":\"{name}\",\"conclusion\":{(conclusion.Length == 0 ? "null" : "\"" + conclusion + "\"")},\"status\":\"{status}\"," +
        $"\"checkSuite\":{{\"workflowRun\":{(workflow == 0 ? "null" : "{\"databaseId\":" + workflow + "}")}}}}}";

    private sealed partial class Gh
    {
        public readonly List<List<string>> Asked = [];
        public Func<string, string?> Answer = _ => "null";
        public (int Exit, string Error)? Fail;

        public ValueTask<RepoHostToolResult> Run(string tool, string directory, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment, CancellationToken token)
        {
            Require(tool == "gh" && args[0] == "api" && args[1] == "graphql", "Only a read-only gh api graphql call is made.");
            Require(!args.Any(arg => arg.Contains("mutation", StringComparison.OrdinalIgnoreCase)), "The query never mutates.");
            var query = args.Single(arg => arg.StartsWith("query=", StringComparison.Ordinal));
            var shas = OidPattern().Matches(query).Select(match => match.Groups[1].Value).ToList();
            Asked.Add(shas);
            if (Fail is { } failure) return ValueTask.FromResult(new RepoHostToolResult(failure.Exit, "", failure.Error));
            var fields = shas.Select((sha, index) => $"\"c{index}\":{Answer(sha) ?? "null"}");
            return ValueTask.FromResult(new RepoHostToolResult(0, "{\"data\":{\"repository\":{" + string.Join(',', fields) + "}}}", ""));
        }

        [GeneratedRegex("object\\(oid: \"([0-9a-f]+)\"\\)")]
        private static partial Regex OidPattern();
    }

    internal static async Task RunAsync()
    {
        var clock = new Clock();
        var gh = new Gh();
        var logs = new List<string>();
        var checks = new GitHubCommitChecks(gh.Run, logs.Add, clock);
        gh.Answer = sha => sha == Sha(1) ? Node(4, 0, 0, Run("build", "SUCCESS"))
            : sha == Sha(2) ? Node(3, 1, 0, Run("build", "SUCCESS") + "," + Run("windows", "FAILURE"))
            : sha == Sha(3) ? Node(2, 0, 2, Run("linux", "", "IN_PROGRESS") + "," + Run("mac", "", "QUEUED"))
            : sha == Sha(5) ? Node(2, 0, 0, Run("a", "CANCELLED"), cancelled: 1)
            : "{\"statusCheckRollup\":null}";

        var all = await checks.ReadAsync("o/r", "/w", [Sha(1), Sha(2), Sha(3), Sha(4), Sha(5), "not-a-sha"], default);
        Require(gh.Asked.Count == 1 && gh.Asked[0].Count == 5, "Five valid commits make one call and a bad id is never sent: " + string.Join('|', gh.Asked.Select(a => a.Count)));
        var byState = all.ToDictionary(item => item.Sha);
        Require(all.Count == 4 && !byState.ContainsKey(Sha(4)), "A commit with no checks is left out.");
        Require(byState[Sha(1)] is { State: "success", Passed: 4, Total: 4, Attention: [] }, "All checks passed is success.");
        Require(byState[Sha(2)] is { State: "failure", Passed: 3, Total: 4 } failing && failing.Attention.SequenceEqual(["windows"]), "One failure is failure and names the check.");
        Require(byState[Sha(3)] is { State: "pending", Passed: 2, Total: 4 } busy && busy.Attention.SequenceEqual(["linux", "mac"]), "Running and queued checks make it pending.");
        Require(byState[Sha(5)].State == "neutral" && byState[Sha(2)].Url == "https://github.com/o/r/commit/" + Sha(2) + "/checks", "Canceled is neutral and the link opens the commit's checks.");

        var calls = gh.Asked.Count;
        await checks.ReadAsync("o/r", "/w", [Sha(1), Sha(2), Sha(3), Sha(4), Sha(5)], default);
        Require(gh.Asked.Count == calls, "A second request for the same commits makes no gh call.");
        clock.Now += TimeSpan.FromSeconds(31);
        await checks.ReadAsync("o/r", "/w", [Sha(1), Sha(2), Sha(3), Sha(4), Sha(5)], default);
        Require(gh.Asked.Count == calls + 1 && gh.Asked[^1].SequenceEqual([Sha(3)]), "After 30 seconds only the running commit is asked again.");
        clock.Now += TimeSpan.FromMinutes(11);
        await checks.ReadAsync("o/r", "/w", [Sha(1)], default);
        Require(gh.Asked.Count == calls + 2, "A finished result is asked again after ten minutes.");

        // The link goes to the workflow run of the first check that needs a look, else the first run; without any run it is the checks page.
        var gh2 = new Gh();
        var linked = new GitHubCommitChecks(gh2.Run, null, clock);
        gh2.Answer = sha => sha == Sha(1) ? Node(3, 1, 0, Run("a", "SUCCESS", workflow: 101) + "," + Run("b", "FAILURE", workflow: 202) + "," + Run("c", "FAILURE", workflow: 303))
            : sha == Sha(2) ? Node(1, 0, 1, Run("a", "SUCCESS", workflow: 11) + "," + Run("b", "", "IN_PROGRESS", 22))
            : sha == Sha(3) ? Node(2, 0, 0, Run("a", "SUCCESS", workflow: 33) + "," + Run("b", "SUCCESS", workflow: 44))
            : Node(1, 0, 0, Run("a", "SUCCESS"));
        var runs = (await linked.ReadAsync("o/r", "/w", [Sha(1), Sha(2), Sha(3), Sha(4)], default)).ToDictionary(item => item.Sha);
        Require(runs[Sha(1)].Url == "https://github.com/o/r/actions/runs/202" && runs[Sha(2)].Url == "https://github.com/o/r/actions/runs/22" && runs[Sha(3)].Url == "https://github.com/o/r/actions/runs/33"
            && runs[Sha(4)].Url == "https://github.com/o/r/commit/" + Sha(4) + "/checks", "The link is the first failing or running check's run, else the first run, else the checks page: " + string.Join(' ', runs.Values.Select(item => item.Url)));

        var batching = new Gh();
        var many = new GitHubCommitChecks(batching.Run, null, clock);
        await many.ReadAsync("o/r", "/w", [.. Enumerable.Range(100, 120).Select(Sha)], default);
        Require(batching.Asked.Select(a => a.Count).SequenceEqual([50, 50, 20]), "120 commits go in batches of at most 50: " + string.Join('|', batching.Asked.Select(a => a.Count)));

        var limited = new Gh { Fail = (1, "gh: API rate limit exceeded (HTTP 403)") };
        var warnings = new List<string>();
        var careful = new GitHubCommitChecks(limited.Run, warnings.Add, clock);
        Require((await careful.ReadAsync("o/r", "/w", [Sha(1)], default)).Count == 0 && warnings.Count == 1, "A rate limit answers nothing and is logged once.");
        await careful.ReadAsync("o/r", "/w", [Sha(2)], default);
        Require(limited.Asked.Count == 1, "While backing off, no further call is made.");
        clock.Now += TimeSpan.FromMinutes(3);
        await careful.ReadAsync("o/r", "/w", [Sha(2)], default);
        Require(limited.Asked.Count == 2 && warnings.Count == 1, "After the back-off it asks again and does not log twice.");
        clock.Now += TimeSpan.FromMinutes(5);
        limited.Fail = null;
        limited.Answer = _ => Node(1, 0, 0, Run("a", "SUCCESS"));
        Require((await careful.ReadAsync("o/r", "/w", [Sha(2)], default)).Count == 1, "It recovers when GitHub answers again.");

        var offline = new Gh();
        var provider = new GitHubProvider(offline.Run);
        Require((await provider.CommitChecksAsync(new("/w", "https://gitlab.com/o/r.git", "", ""), [Sha(1)], default)).Count == 0 && offline.Asked.Count == 0,
            "A repository without a GitHub remote shows nothing and asks nothing.");
        offline.Answer = _ => Node(1, 0, 0, Run("a", "SUCCESS"));
        Require((await provider.CommitChecksAsync(new("/w", Remote, "", ""), [Sha(1)], default)).Single().State == "success", "The provider answers for a GitHub remote.");
        Require((await new PlainProvider().CommitChecksAsync(new("/w", Remote, "", ""), [Sha(1)], default)).Count == 0, "A provider that does not know checks, or none at all, shows nothing.");
        Console.WriteLine("PASS commit checks host: states, batching, caching, back-off, no remote and no provider");
    }

    private sealed class PlainProvider : RepoHostProvider
    {
        public override bool Recognizes(string remoteUrl) => true;
        public override ValueTask<RepoHostSignIn> SignInAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask<PullRequest?> FindAsync(RepoHostBranch branch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask<RepoHostOutcome> CreateOrUpdateAsync(RepoHostBranch branch, RepoHostRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask ReadyAsync(RepoHostBranch branch, PullRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask ReopenAsync(RepoHostBranch branch, PullRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask MergeAsync(RepoHostBranch branch, PullRequest request, string method, string head, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override string CompareUrl(RepoHostBranch branch, RepoHostRequest request) => "";
    }

    public class Opens : DispatchProxy
    {
        internal readonly List<string> Calls = [];
        internal bool Handled;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name + "(" + string.Join(", ", args ?? []) + ")");
            return method.ReturnType == typeof(ValueTask<bool>) ? new ValueTask<bool>(Handled) : method.ReturnType == typeof(ValueTask) ? default(ValueTask) : null;
        }
    }

    /// <summary>A left click on the icon asks the repo host to open the link in its own tab, the browser only when it has none; the GitHub side opens a run link in its Actions tab and leaves a checks page to the browser.</summary>
    private static void Clicking()
    {
        const string RunLink = "https://github.com/o/r/actions/runs/202";
        var commit = new GitGraphCommit(Sha(9), Sha(9)[..7], [], [], "subject", "ann", "2026-11-01T10:00:00Z");
        var (rows, _) = GraphLanes.Layout([commit], null);
        var proxy = DispatchProxy.Create<IPluginUIContext, Opens>();
        var context = (Opens)(object)proxy;
        var browser = new List<Uri>();
        var row = new GraphCommitRow(null!, () => "w", rows[0], 1, 1, [], _ => { }, _ => { }, url => GraphPanel.OpenChecksAsync(proxy, "w", url, uri => { browser.Add(uri); return Task.CompletedTask; }).Wait());
        row.SetChecks(new(Sha(9), "failure", 0, 1, ["lint"], RunLink));
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        void Click() => row.Checks.RaiseEvent(new PointerPressedEventArgs(row.Checks, pointer, row.Checks, new Point(1, 1), 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));

        context.Handled = true;
        Click();
        Require(context.Calls.SequenceEqual(["OpenRepoHostLinkAsync(w, " + RunLink + ")"]) && browser.Count == 0, "The click asks the repo host to open the run, not the browser: " + string.Join('|', context.Calls) + " browser " + browser.Count);
        context.Handled = false;
        Click();
        Require(browser.Select(uri => uri.AbsoluteUri).SequenceEqual([RunLink]), "With no repo host tab the same click opens the browser.");
        row.SetChecks(new(Sha(9), "failure", 0, 1, [], ""));
        context.Calls.Clear();
        Click();
        Require(context.Calls.Count == 0 && browser.Count == 1, "A commit without a link does nothing.");

        var github = (Opens)(object)DispatchProxy.Create<IPluginUIContext, Opens>();
        var opened = Tereditor.Plugins.GitHub.UI.GitHubUI.OpenLinkAsync((IPluginUIContext)(object)github, "w", RunLink).AsTask().GetAwaiter().GetResult();
        Require(opened && github.Calls.SequenceEqual(["OpenDocumentAsync(w, github, run:202, Run 202)"]), "GitHub opens a run link in its own tab, on the run: " + string.Join('|', github.Calls));
        github.Calls.Clear();
        Require(!Tereditor.Plugins.GitHub.UI.GitHubUI.OpenLinkAsync((IPluginUIContext)(object)github, "w", "https://github.com/o/r/commit/" + Sha(9) + "/checks").AsTask().GetAwaiter().GetResult() && github.Calls.Count == 0, "A checks page is not a GitHub tab, so the browser gets it.");
        Console.WriteLine("PASS commit checks click: the icon asks the repo host to open the run (not the browser), falls back to the browser, and GitHub opens its Actions run tab");
    }

    /// <summary>Headless: a commit row shows each icon with its tooltip, and nothing when there are no checks.</summary>
    internal static void RunUi()
    {
        var commit = new GitGraphCommit(Sha(9), Sha(9)[..7], [], [], "subject", "ann", "2026-11-01T10:00:00Z");
        var (rows, _) = GraphLanes.Layout([commit], null);
        var row = new GraphCommitRow(null!, () => "w", rows[0], 1, 1, [], _ => { }, _ => { }, _ => { });
        Require(!row.Checks.IsVisible, "A commit without checks has no icon.");
        string[] names = ["a", "b", "c", "d", "e", "f", "g"];
        foreach (var (state, glyph, tip) in new[]
        {
            ("success", "checkboxCircleFill", "CI: 3 of 4 checks passed"), ("failure", "closeCircleFill", "CI: 3 of 4 checks passed"),
            ("pending", "loader", "CI: 3 of 4 checks passed"), ("neutral", "recordCircle", "CI: 3 of 4 checks passed")
        })
        {
            row.SetChecks(new(Sha(9), state, 3, 4, ["lint"], "https://github.com/o/r/commit/" + Sha(9) + "/checks"));
            Require(row.Checks.IsVisible && row.Checks.Glyph == glyph && row.Checks.Tip == tip + "\nlint", $"The {state} icon is {glyph} with its tooltip, not {row.Checks.Glyph} and '{row.Checks.Tip}'.");
        }
        row.SetChecks(new(Sha(9), "failure", 0, 7, names, ""));
        Require(row.Checks.Tip == "CI: 0 of 7 checks passed\na\nb\nc\nd\ne\nand 2 more", "At most five names are listed, then 'and N more'.");
        row.SetChecks(null);
        Require(!row.Checks.IsVisible, "The icon goes when the checks do.");
        Clicking();
        Console.WriteLine("PASS commit checks UI: each state's icon and tooltip, none without checks");
    }
}