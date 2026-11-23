using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks;

/// <summary>Headless: the Actions tab and a run's page with fake data: sidebar, pinned header, virtualized run list and log lines, paging, polls and expand state.</summary>
internal static partial class GitHubActionsChecks
{
    private const int Count = 5000;
    private static readonly string[] Workflows = ["5", "6", "7"];

    [GeneratedRegex(@"^GitHubRun\d+$")]
    private static partial Regex RowName();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump(Func<bool> done, string what)
    {
        var end = DateTime.UtcNow.AddSeconds(20);
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > end) throw new InvalidOperationException("Timed out waiting for " + what);
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Layout(Window window)
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    private static Control? Named(Control root, string name) => root.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name);

    private static List<Border> LiveRows(Control root) => [.. root.GetVisualDescendants().OfType<Border>().Where(b => b.Name is { } n && RowName().IsMatch(n) && b.IsEffectivelyVisible)];

    private static string Text(Control root) => string.Concat(root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Inlines is { Count: > 0 } ? string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) : t.Text));

    private static GitHubRun Make(int index, string suffix = "")
    {
        var id = (9000 - index).ToString();
        return index switch
        {
            0 => new(id, "Fix the thing", "running", "CI", "5", ".github/workflows/ci.yml", 400, "push", "ann", "abcdef1234", "main", "2026-11-18T13:33:00Z", "", "https://github.com/o/r/actions/runs/" + id),
            1 => new(id, "Break the thing", "failure", "Nightly", "7", ".github/workflows/nightly.yml", 12, "schedule", "bob", "1234567abc", "main", "2026-11-17T20:42:00Z", "1m 4s", "https://github.com/o/r/actions/runs/" + id),
            2 => new(id, string.Join(' ', Enumerable.Repeat("A very long run title that has to wrap", 8)), "success", "CI", "5", ".github/workflows/ci.yml", 399, "push", "ann", "9999999abc", "feature/long-branch-name", "2026-11-17T09:00:00Z", "2m 5s", "https://github.com/o/r/actions/runs/" + id),
            3 => new(id, "Update dependencies", "success", "Dependabot Updates", "6", "dynamic/dependabot/dependabot-updates", 77, "dynamic", "dependabot[bot]", "", "main", "2026-11-16T09:00:00Z", "30s", "https://github.com/o/r/actions/runs/" + id),
            _ => new(id, "Run " + index + suffix, index % 7 == 0 ? "failure" : "success", "CI", "5", ".github/workflows/ci.yml", 400 - index, "pull_request", "ann", "abcdef1234", "main", "2026-11-10T09:00:00Z", "1m 1s", "https://github.com/o/r/actions/runs/" + id)
        };
    }

    public static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
        var utc = TimeZoneInfo.Utc;
        var plus = TimeZoneInfo.CreateCustomTimeZone("plus", TimeSpan.FromHours(1), "plus", "plus");
        var clock = DateTimeOffset.Parse("2026-11-18T14:00:00Z");
        Require(GitHubRuns.Started("2026-11-18T13:33:00Z", clock, plus) == "Today at 2:33 PM", "A run from today reads 'Today at 2:33 PM'");
        Require(GitHubRuns.Started("2026-11-17T20:42:00Z", clock, plus) == "Nov 17, 9:42 PM GMT+1", "An older run reads the date, the time and the zone offset");
        var (found, free) = GitHubActionsTab.ParseSearch("branch:main status:failure fix bug nosuch:x");
        Require(found.Count == 2 && found["branch"] == "main" && found["status"] == "failure" && free == "fix bug nosuch:x", "The search box splits runs qualifiers from free text");
        Require(GitHubLogView.Plain("\x1b[36;1mmake\x1b[0m done") == "make done" && GitHubLogView.Runs("\x1b[31mred\x1b[0m plain", Ui.TextBrush).ToList() is [{ Text: "red" } red, { Text: " plain" }] && ReferenceEquals(red.Foreground, Ui.Danger),
            "Colour codes become runs in the theme's own colours");

        var version = 0;
        var requests = new List<GitHubViewParams>();
        Task<GitHubView> Load(GitHubViewParams p, CancellationToken token)
        {
            requests.Add(p);
            var revision = $"v{version}-{p.Page}-{string.Join(',', p.Filters.Select(f => f.Key + f.Value))}";
            if (p.Revision == revision) return Task.FromResult(new GitHubView(true, revision, "", "", "none", [], [], [], false) { Total = Count });
            var runs = Enumerable.Range((p.Page - 1) * p.Limit, p.Limit).Where(i => i < Count).Select(i => Make(i, i == 9 && version > 0 ? " changed" : "")).ToList();
            var workflow = p.Filters.FirstOrDefault(f => f.Key == "workflow")?.Value ?? "";
            GitHubWorkflow[] known = [new("5", "CI", ".github/workflows/ci.yml", "active"), new("6", "Dependabot Updates", "dynamic/dependabot/dependabot-updates", "active"), new("7", "Slow checks", ".github/workflows/slow.yml", "disabled_manually")];
            return Task.FromResult(new GitHubView(false, revision, known.FirstOrDefault(w => w.Id == workflow)?.Name ?? "All workflows", workflow.Length > 0 ? "Showing runs from CI" : "Showing runs from all workflows", "none", [], [],
                [new("workflow", "Workflow", [.. known.Select(w => w.Name)], known.FirstOrDefault(w => w.Id == workflow)?.Name ?? ""), new("event", "Event", ["push"], ""), new("status", "Status", ["failure"], ""), new("branch", "Branch", ["main"], ""), new("actor", "Actor", ["ann"], "")],
                p.Page * p.Limit < Count, Count.ToString("N0") + " workflow runs")
            { Runs = runs, Workflows = p.Page == 1 ? known : [], RepoUrl = "https://github.com/o/r", Total = Count });
        }

        var baseline = GC.GetAllocatedBytesForCurrentThread();
        var tab = new GitHubActionsTab(null, "w", Load, () => clock, utc);
        var window = new Window { Width = 1100, Height = 700, Content = tab };
        window.Show();
        Pump(() => tab.RunCount >= GitHubActionsTab.PageSize, "the first page of runs");
        Layout(window);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - baseline;
        Require(allocated < 256L * 1024 * 1024, "Building the first page allocates under 256 MB on the UI thread (" + allocated / 1024 / 1024 + " MB)");
        Require(requests[0].Page == 1 && requests[0].Limit == GitHubActionsTab.PageSize, "The first request asks for one page");
        Require(Named(tab, GitHubWebLink.ButtonName) is Button { IsVisible: true, Tag: "https://github.com/o/r/actions" } open && ToolTip.GetTip(open) is string && AutomationProperties.GetName(open) == "Open in browser", "The Actions list offers Open in browser to https://github.com/o/r/actions");
        Require(Named(tab, "GitHubWorkflowAll") is { } && Named(tab, "GitHubWorkflow5") is { } && Named(tab, "GitHubWorkflow7") is { }, "The sidebar lists All workflows and every workflow");
        Require(Named(tab, "GitHubWorkflowAll") is Border { Background: var all } && ReferenceEquals(all, Ui.Selected), "All workflows is selected at first");
        Require(Text(tab).Contains("Showing runs from all workflows", StringComparison.Ordinal) && Text(tab).Contains("5,000 workflow runs", StringComparison.Ordinal) && Text(tab).Contains("Auto-refresh: 30s", StringComparison.Ordinal), "The header shows the subtitle, the count and the auto-refresh choice");
        Require(Named(tab, "GitHubRunSearch") is TextBox { PlaceholderText: "Search runs" }, "The search box says Search runs");
        Require(Named(tab, "GitHubChipWorkflow") is { } && Named(tab, "GitHubChipActor") is { }, "The table header offers the filter dropdowns");
        Require(tab.Polling, "Auto-refresh runs by default");
        tab.SetAutoRefresh(0);
        Require(!tab.Polling, "Auto-refresh off stops the poll");
        tab.SetAutoRefresh(60);
        Require(tab.Polling, "Auto-refresh 60s polls again");

        var running = Named(tab, "GitHubRun9000");
        var failed = Named(tab, "GitHubRun8999");
        var longRun = Named(tab, "GitHubRun8998");
        Require(running is not null && failed is not null && longRun is not null, "The running, failed and long runs are in view");
        Require(Text(running!).Contains("Fix the thing", StringComparison.Ordinal) && Text(running!).Contains("CI #400: Commit abcdef1 pushed by ann", StringComparison.Ordinal) && Text(running!).Contains("Today at 1:33 PM", StringComparison.Ordinal), "A running run shows its title, trigger and start");
        Require(Text(failed!).Contains("Nightly #12: Scheduled", StringComparison.Ordinal) && Text(failed!).Contains("1m 4s", StringComparison.Ordinal) && Text(failed!).Contains("Nov 17, 8:42 PM GMT+0", StringComparison.Ordinal), "A failed run shows its schedule, duration and date");
        Require(Named(failed!, "GitHubRunBranch") is not null && Named(running!, "GitHubRunMore") is not null, "A run row has a branch chip and a menu");
        var heading = (TextBlock)Named(longRun!, "GitHubRunTitle")!;
        Require(heading is { MaxLines: 2, TextWrapping: TextWrapping.Wrap } && heading.Bounds.Height <= heading.FontSize * 1.7 * 2 && heading.Bounds.Height > heading.FontSize * 1.7, "A long title wraps to two lines and ends with an ellipsis");

        var header = Named(tab, "GitHubRunTableHeader")!;
        var top = header.TranslatePoint(new Point(0, 0), tab)!.Value.Y;
        var title = Named(tab, "GitHubRunSearch")!.TranslatePoint(new Point(0, 0), tab)!.Value.Y;
        var guard = 0;
        while (tab.RunCount < GitHubActionsTab.MaxRuns && guard++ < 60)
        {
            var before = tab.RunCount;
            tab.Scroller.ScrollToEnd();
            Layout(window);
            Pump(() => tab.RunCount > before || guard > 55, "the next page");
            Layout(window);
        }
        Require(tab.RunCount == GitHubActionsTab.MaxRuns, "Paging stops at the cap of " + GitHubActionsTab.MaxRuns + " runs (got " + tab.RunCount + ")");
        Require(requests.Max(r => r.Page) == GitHubActionsTab.MaxRuns / GitHubActionsTab.PageSize + 1 || requests.Max(r => r.Page) == GitHubActionsTab.MaxRuns / GitHubActionsTab.PageSize, "Pages are read one at a time as the list is scrolled");
        tab.Scroller.ScrollToEnd(); Layout(window);
        Require(header.TranslatePoint(new Point(0, 0), tab)!.Value.Y == top && Named(tab, "GitHubRunSearch")!.TranslatePoint(new Point(0, 0), tab)!.Value.Y == title, "The header, search and table header stay pinned after scrolling to the end");
        Require(tab.Items[^1] is not GitHubRun && Text(tab).Contains("Showing the latest 500 runs", StringComparison.Ordinal), "The capped list says to refine the search");
        var live = LiveRows(tab);
        Require(live.Count is > 0 and < 60, "At the end of 500 runs only " + live.Count + " row controls are live (under 60)");
        var viewport = tab.Scroller.Viewport.Height;
        tab.Scroller.Offset = new Vector(0, (tab.Scroller.Extent.Height - viewport) / 2);
        Layout(window);
        live = LiveRows(tab);
        var indexes = live.Select(row => tab.Items.ToList().FindIndex(item => item is GitHubRun run && "GitHubRun" + run.Id == row.Name)).ToList();
        Require(live.Count is > 0 and < 60 && indexes.All(i => i >= 0), "In the middle only " + live.Count + " row controls are live (under 60)");
        Require(Math.Abs(indexes.Average() - tab.RunCount / 2.0) < 60, "The rows in view in the middle are the middle items (mean index " + indexes.Average() + ")");
        Require(live.All(row => Text(row).Contains("Run " + indexes[live.IndexOf(row)], StringComparison.Ordinal) || indexes[live.IndexOf(row)] < 4), "Each live row shows its own item");

        var events = new List<NotifyCollectionChangedEventArgs>();
        tab.Items.CollectionChanged += (_, e) => events.Add(e);
        version = 1;
        var poll = tab.PollAsync();
        Pump(() => poll.IsCompleted, "a poll");
        Require(events.Count == 1 && events[0].Action == NotifyCollectionChangedAction.Replace && ((GitHubRun)events[0].NewItems![0]!).Title == "Run 9 changed", "A poll with one changed run replaces one row");
        var again = tab.PollAsync();
        Pump(() => again.IsCompleted, "a second poll");
        Require(events.Count == 1, "A poll with nothing new changes no row");

        tab.Choose("workflow", "5");
        Pump(() => requests.Any(r => r.Filters.Any(f => f is { Key: "workflow", Value: "5" })) && tab.RunCount > 0 && tab.RunCount <= GitHubActionsTab.PageSize, "the filtered list");
        Layout(window);
        Require(Text(tab).Contains("Showing runs from CI", StringComparison.Ordinal) && Named(tab, "GitHubWorkflow5") is Border { Background: var picked } && ReferenceEquals(picked, Ui.Selected), "Choosing a workflow filters the list and selects it in the sidebar");
        tab.SetSidebar(false); Layout(window);
        Require(Named(tab, "GitHubSidebar") is { IsEffectivelyVisible: false }, "Collapse sidebar hides the sidebar");
        tab.SetSidebar(true);
        window.Close();
        Console.WriteLine("PASS GitHub Actions tab: sidebar, pinned header, paging, virtualized rows, polls (" + (allocated / 1024 / 1024) + " MB allocated on the UI thread for the first page)");

        RunPage();
    }

    private static GitHubView RunView(string revision, string jobState)
    {
        GitHubRow[] rows =
        [
            new("1", "build", jobState, "", [], "2m 5s", "https://github.com/o/r/actions/runs/9/job/1", "2026-11-01T10:00:00Z"),
            new("1/1", "Set up job", "success", "", [], "1s", "", "2026-11-01T10:00:00Z", 1),
            new("1/2", "Build", jobState == "running" ? "running" : "failure", "", [], jobState == "running" ? "" : "1m", "", "2026-11-01T10:00:03Z", 1),
            new("1/3", "Post job", jobState == "running" ? "queued" : "skipped", "", [], "", "", "", 1),
            new("2", "lint", "success", "", [], "10s", "https://github.com/o/r/actions/runs/9/job/2", "2026-11-01T10:00:00Z"),
            new("2/1", "Lint", "success", "", [], "10s", "", "2026-11-01T10:00:00Z", 1)
        ];
        return new(false, revision, "Fix it #4", "CI · Commit abcdef1 pushed by ann", jobState == "running" ? "running" : "failure", rows, [], [], false, "https://github.com/o/r/actions/runs/9");
    }

    private static GitHubLog BigLog(int lines)
    {
        var setup = new GitHubLogStep(1, "Set up job", [new GitHubLogLine("10:00:00", "Runner started")], false, 0);
        var build = new List<GitHubLogLine> { new("10:00:03", "Run make") { Header = true } };
        build.AddRange(Enumerable.Range(0, lines).Select(i => new GitHubLogLine("10:00:04", i == lines - 1 ? "boom" : "\x1b[32mline " + i + "\x1b[0m") { Grouped = true, Error = i == lines - 1 }));
        return new([setup, new GitHubLogStep(2, "Build", build, true, 0), new GitHubLogStep(3, "Post job", [], false, 0)], 0, "https://github.com/o/r/actions/runs/9/job/1");
    }

    private static void RunPage()
    {
        var revision = 0;
        var logRequests = new List<string>();
        var clock = DateTimeOffset.Parse("2026-11-01T10:00:20Z");
        var state = "failure";
        var tab = new GitHubRunTab(null, "w", "run:9", (p, token) => Task.FromResult(RunView("r" + revision, state)),
            (job, token) => { logRequests.Add(job); return Task.FromResult(state == "running" ? new GitHubLog([], 0, "", "This job is still running.", true) : BigLog(100_000)); }, () => clock);
        var window = new Window { Width = 1100, Height = 700, Content = tab };
        window.Show();
        var load = tab.LoadAsync();
        Pump(() => load.IsCompleted, "the run");
        Layout(window);
        Require(Text(tab).Contains("Fix it #4", StringComparison.Ordinal) && Named(tab, "GitHubJob1") is { } && Named(tab, "GitHubJob2") is { }, "The run page lists its jobs");
        Require(tab.SelectedJob == "1" && Named(tab, "GitHubStep1_2") is not null, "The failed job is selected and shows its steps");
        Require(Named(tab, GitHubWebLink.ButtonName) is Button { IsVisible: true, Tag: "https://github.com/o/r/actions/runs/9" } open && ToolTip.GetTip(open) is string && AutomationProperties.GetName(open) == "Open in browser", "A run page offers Open in browser to https://github.com/o/r/actions/runs/9");
        Pump(() => logRequests.Count > 0 && Named(tab, "GitHubStepLog") is not null, "the failed step's log");
        Layout(window);
        Require(tab.GetVisualDescendants().OfType<GitHubLogView>().Count() == 1, "Only the failed step is open at first");
        var log = tab.GetVisualDescendants().OfType<GitHubLogView>().Single();
        Require(log.Rows.Count == 100_001 && log.Height < 500, "A 100,000 line log is data: one row per line, a bounded viewport");
        var rows = log.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.FontFamily == Ui.CodeFont).ToList();
        Require(rows.Count is > 0 and < 60, "Only the visible log lines are controls (" + rows.Count + ")");
        Require(Text(log).Contains("Run make", StringComparison.Ordinal) && Text(log).Contains("line 0", StringComparison.Ordinal) && !Text(log).Contains('\x1b'), "Log lines show with line numbers and no raw colour codes");
        log.Toggle(0, false);
        Layout(window);
        Require(log.Rows.Count == 1, "Closing a group hides its lines");
        log.Toggle(0, true);

        tab.ToggleStep("1", 1);
        Layout(window);
        Require(tab.GetVisualDescendants().OfType<GitHubLogView>().Count() == 2, "A click opens a step and shows its log lines");
        revision++;
        var refresh = tab.LoadAsync();
        Pump(() => refresh.IsCompleted, "a refresh");
        Layout(window);
        Require(tab.GetVisualDescendants().OfType<GitHubLogView>().Count() == 2, "Open steps stay open after a refresh");
        tab.SetAll(false);
        Layout(window);
        Require(!tab.GetVisualDescendants().OfType<GitHubLogView>().Any(), "Collapse all hides every log");
        revision++;
        var later = tab.LoadAsync();
        Pump(() => later.IsCompleted, "a refresh after collapsing");
        Layout(window);
        Require(!tab.GetVisualDescendants().OfType<GitHubLogView>().Any(), "A collapsed failed step stays collapsed after a refresh");
        tab.SetAll(true);
        Layout(window);
        Require(tab.GetVisualDescendants().OfType<GitHubLogView>().Count() == 2, "Expand all opens the steps that have lines");
        tab.Select("2"); Layout(window);
        Require(Named(tab, "GitHubStep2_1") is not null && Named(tab, "GitHubStep1_2") is null, "Choosing a job shows that job's steps");
        window.Close();

        state = "running";
        revision++;
        var running = new GitHubRunTab(null, "w", "run:9", (p, token) => Task.FromResult(RunView("r" + revision, state)), (job, token) => Task.FromResult(new GitHubLog([], 0, "", "This job is still running.", true)), () => clock);
        var second = new Window { Width = 1100, Height = 700, Content = running };
        second.Show();
        var wait = running.LoadAsync();
        Pump(() => wait.IsCompleted, "the running run");
        running.ToggleStep("1", 2);
        running.SettleAsync().GetAwaiter().GetResult();
        Layout(second);
        Require(Text(running).Contains("Not ready yet", StringComparison.Ordinal) && Text(running).Contains("Step time so far: 17s", StringComparison.Ordinal), "A step of a running job says its log is not ready, with the time so far");
        second.Close();
        Console.WriteLine("PASS GitHub run page: jobs, steps, virtualized logs, expand state across refreshes, Collapse all, running job");
    }
}