using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.Host;
using Tereditor.Plugins.GitHub.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks;

/// <summary>The GitHub issue and pull request browser against canned gh output; nothing reaches a network.</summary>
internal static class GitHubBrowserChecks
{
    private const string Repo = """{"owner":{"login":"o"},"name":"r","url":"https://github.com/o/r","hasDiscussionsEnabled":true}""";

    private const string Item = """
        {"__typename":"PullRequest","number":7,"title":"Add b","state":"MERGED","url":"https://github.com/o/r/pull/7","createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-02T10:00:00Z","closedAt":"2026-11-02T10:00:00Z",
         "author":{"login":"ann"},"labels":{"nodes":[{"name":"bug","color":"d73a4a"}]},"comments":{"totalCount":3},"isDraft":false,"reviewDecision":"APPROVED",
         "commits":{"nodes":[{"commit":{"statusCheckRollup":{"state":"SUCCESS"}}}]}}
        """;

    private static string ListJson(string extra = "") => """{"data":{"list":{"pageInfo":{"hasNextPage":true},"nodes":[""" + Item + extra + """]},"open":{"issueCount":4},"closed":{"issueCount":9}}}""";

    private static string DetailJson(bool more, string comment) => DetailTemplate.Replace("@MORE@", more ? "true" : "false").Replace("@COMMENT@", comment);

    private const string DetailTemplate = """
        {"data":{"repository":{"issueOrPullRequest":{"__typename":"PullRequest","number":7,"title":"Add b","state":"OPEN","url":"https://github.com/o/r/pull/7","createdAt":"2026-11-01T10:00:00Z",
         "author":{"login":"ann"},"labels":{"nodes":[]},"comments":{"totalCount":1},"body":"Body text","assignees":{"nodes":[{"login":"bob"}]},"milestone":null,
         "baseRefName":"main","headRefName":"feature","additions":5,"deletions":2,"changedFiles":1,"mergeStateStatus":"BLOCKED","isDraft":true,"reviewDecision":"REVIEW_REQUIRED",
         "commits":{"nodes":[{"commit":{"statusCheckRollup":{"state":"FAILURE","contexts":{"nodes":[{"__typename":"CheckRun","name":"build","conclusion":"FAILURE","status":"COMPLETED"},{"__typename":"StatusContext","context":"ci","state":"SUCCESS"}]}}}}]},
         "timelineItems":{"pageInfo":{"hasNextPage":@MORE@,"endCursor":"c1"},"nodes":[
         {"__typename":"PullRequestCommit","commit":{"oid":"abcdef123456","messageHeadline":"Add b","authoredDate":"2026-11-01T10:01:00Z","author":{"name":"Ann","user":{"login":"ann"}}}},
         {"__typename":"IssueComment","author":{"login":"bob"},"createdAt":"2026-11-01T11:00:00Z","body":"@COMMENT@","authorAssociation":"MEMBER"},
         {"__typename":"PullRequestReview","author":{"login":"bob"},"createdAt":"2026-11-01T12:00:00Z","state":"CHANGES_REQUESTED","body":"Fix","comments":{"nodes":[{"path":"b.txt","body":"Rename"}]}},
         {"__typename":"LabeledEvent","actor":{"login":"ann"},"createdAt":"2026-11-01T13:00:00Z","label":{"name":"bug","color":"d73a4a"}},
         {"__typename":"SomethingNew","createdAt":"2026-11-01T14:00:00Z"}]}}}}}
        """;

    private static readonly string JobLog = string.Join('\n',
        "2026-11-01T10:00:00.1234567Z ##[group]Run build",
        "2026-11-01T10:00:01.0000000Z \x1b[36;1mmake\x1b[0m",
        "2026-11-01T10:00:02.0000000Z ##[error]Process completed with exit code 2.",
        "2026-11-01T10:00:02.5000000Z ##[endgroup]",
        "2026-11-01T10:00:03.0000000Z Post job cleanup.");

    private static Control? Named(Control root, string name) => root.GetLogicalDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Headless: every icon the GitHub screens name resolves, and the pane, list rows, conversation and generic views build with fake data.</summary>
    public static void RunUi()
    {
        var names = typeof(GitHubIcons).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Require(names.Count > 10, "The GitHub icon list is not empty");
        foreach (var name in names)
            try { Ui.Icon(name); }
            catch (Exception error) { throw new InvalidOperationException($"The GitHub icon '{name}' does not resolve: {error.Message}"); }

        var pane = new GitHubPane(null!, "w");
        Require(pane.Children.Count == 9, "The GitHub pane builds its nine buttons");

        static GitHubItem Item(int number, string state, bool draft) => new(number, "Title " + number, state, draft, "ann", [new("bug", "d73a4a")], 2, "2026-11-01T10:00:00Z", "2026-11-02T10:00:00Z", null, "https://github.com/o/r/pull/" + number, "passing", "APPROVED");
        foreach (var kind in new[] { GitHubTab.Issues, GitHubTab.Pulls })
        {
            var tab = new GitHubTab(null!, "w", kind);
            tab.RenderList(new(false, "r", "o/r", [Item(1, "open", false), Item(2, "open", true), Item(3, "closed", false), Item(4, "merged", false)], 2, 2, true) { RepoLabels = [new("bug", "d73a4a")] });
            Require(Named(tab, "GitHubRows") is Panel { Children.Count: 5 }, "The " + kind + " list builds a row per item");
        }

        string[] eventKinds = ["comment", "review", "commit", "labeled", "unlabeled", "closed", "reopened", "merged", "assigned", "unassigned", "renamed", "milestoned", "referenced", "review-requested", "ready", "draft", "force-pushed", "branch-deleted", "SomethingNew"];
        var timeline = eventKinds.Select(k => new GitHubEvent(k, "bob", "2026-11-01T11:00:00Z", "Text", "abc", "d73a4a", [new("b.txt", "Rename")])).ToList();
        foreach (var kind in new[] { GitHubTab.Issues, GitHubTab.Pulls })
        {
            var tab = new GitHubTab(null!, "w", kind);
            var pull = kind == GitHubTab.Pulls ? new GitHubPullFacts("main", "feature", 5, 2, 1, "APPROVED", "BLOCKED", "failing", [new("build", "failing"), new("ci", "passing"), new("lint", "pending")]) : null;
            tab.RenderDetail(new(false, "r", "o/r", kind, 7, "Add b", "open", true, "ann", "2026-11-01T10:00:00Z", "https://github.com/o/r/pull/7", "Body", [new("bug", "d73a4a")], ["bob"], "M1", timeline, true, pull));
            Require(Named(tab, "GitHubConversation") is Panel, "The " + kind + " conversation builds every timeline kind");
        }

        string[] states = ["success", "failure", "cancelled", "skipped", "running", "queued", "closed", "answered", "open", "none"];
        var rows = states.Select(s => new GitHubRow("9", "Row " + s, s, "meta", [new("chip", "238636")], "right", "https://github.com/o/r/actions/runs/9", "2026-11-01T10:00:00Z")).ToList();
        foreach (var kind in new[] { "runs", "discussions", "releases", "contributors", "deployments", "packages" })
        {
            var tab = new GitHubViewTab(null!, "w", kind);
            tab.Render(new(false, "r", "Title", "Sub", "none", rows, [new("Card", "ann", "2026-11-01T10:00:00Z", "note", "## Notes")], [new("branch", "Branch", ["main"], "")], true, "https://github.com/o/r"));
            Require(Named(tab, "GitHubView") is Panel, "The " + kind + " view builds a row for every state");
        }
        foreach (var state in states)
        {
            var tab = new GitHubViewTab(null!, "w", "run:9");
            tab.Render(new(false, "r", "Run", "Sub", state, rows, [], [], false));
        }
        Console.WriteLine("PASS GitHub pane, rows, conversation and views render with every icon resolved");
        LinkHoverAndAvatars();
        PlaceholdersAreCentred();
    }

    private static Window Show(Control content, double width = 800, double height = 600)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Frame(window);
        return window;
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    }

    /// <summary>A tab's scroller, taken out of the tab so a window can host it without the tab's refresh timer or host connection.</summary>
    private static GitHubPage Scroller(Border tab)
    {
        var page = (GitHubPage)tab.Child!;
        tab.Child = null;
        return page;
    }

    private static void LinkHoverAndAvatars()
    {
        var people = (int count) => (IReadOnlyList<GitHubPerson>)[.. Enumerable.Range(0, count).Select(i => new GitHubPerson("user" + i, ""))];
        GitHubItem Make(int number, int assignees, string color) =>
            new(number, "Title " + number, "open", false, "ann", [new("l", color)], 0, "2026-11-01T10:00:00Z", "2026-11-02T10:00:00Z", null, "https://github.com/o/r/issues/" + number, "none", "none") { Assignees = people(assignees) };
        var tab = new GitHubTab(null!, "w", GitHubTab.Issues);
        tab.RenderList(new(false, "r", "o/r", [Make(1, 2, "ffffff"), Make(2, 0, "000000"), Make(3, 4, "d73a4a")], 3, 0, false));
        var window = Show(Scroller(tab));

        var title = (TextBlock)Named(window, "GitHubTitle1")!;
        var row = Named(window, "GitHubRow1")!;
        Require(title.TextDecorations is null && ReferenceEquals(title.Foreground, Ui.TextBrush), "A row title is plain text colour at rest");
        var inside = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(new Point(0, 0)); Frame(window);
        window.MouseMove(inside); Frame(window);
        Require(title.TextDecorations is { Count: > 0 } && ReferenceEquals(title.Foreground, Ui.Accent), "Hovering a row underlines its title and turns it accent");
        Require(row is Border { Background: var fill } && ReferenceEquals(fill, Ui.Hover), "Hovering a row also fills the row");
        window.MouseMove(new Point(0, 0)); Frame(window);
        Require(title.TextDecorations is null && ReferenceEquals(title.Foreground, Ui.TextBrush), "Leaving the row restores the title");

        Require(Named(window, "GitHubRow1")!.GetLogicalDescendants().OfType<Panel>().FirstOrDefault(p => p.Name == "GitHubAssignees") is { Children.Count: 2 }, "Two assignees show two avatars");
        Require(Named(window, "GitHubRow2")!.GetLogicalDescendants().All(c => c is not Control { Name: "GitHubAssignees" }), "No assignees show no avatars");
        var four = Named(window, "GitHubRow3")!.GetLogicalDescendants().OfType<Panel>().First(p => p.Name == "GitHubAssignees");
        Require(four.Children.Count == 4 && four.Children[3].GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "+1"), "Four assignees show three avatars and +1");
        Require(Named(window, "GitHubAvataruser0") is { } avatar && ToolTip.GetTip(avatar) as string == "user0", "An avatar's tooltip is the login");
        static IBrush? Ink(GitHubLabel label) => ((TextBlock)((Border)GitHubTab.LabelChip(label)).Child!).Foreground;
        Require(ReferenceEquals(Ink(new("l", "ffffff")), Brushes.Black) && ReferenceEquals(Ink(new("l", "000000")), Brushes.White), "A light label has dark text and a dark label has light text");
        window.Close();
        Console.WriteLine("PASS GitHub row titles hover to accent and underline; assignee avatars and label chips render");
    }

    private static void PlaceholdersAreCentred()
    {
        foreach (var tab in new Border[] { new GitHubTab(null!, "w", GitHubTab.Issues), new GitHubViewTab(null!, "w", "runs") })
        {
            var scroll = Scroller(tab);
            var window = Show(scroll);
            var text = scroll.Scroll.GetLogicalDescendants().OfType<TextBlock>().First();
            var centre = text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height / 2), window)!.Value;
            Require(Math.Abs(centre.X - window.Bounds.Width / 2) <= 1 && Math.Abs(centre.Y - window.Bounds.Height / 2) <= 1, tab.GetType().Name + " centres its loading text both ways, got " + centre);
            window.Close();
        }
        Console.WriteLine("PASS GitHub tabs centre their loading text");
        PinnedHeader();
    }

    private static void PinnedHeader()
    {
        var items = Enumerable.Range(1, 40).Select(i => new GitHubItem(i, "Title " + i, "open", false, "ann", [], 0, "2026-11-01T10:00:00Z", "2026-11-02T10:00:00Z", null, "u", "none", "none")).ToList();
        var tab = new GitHubTab(null!, "w", GitHubTab.Issues);
        tab.RenderList(new(false, "r", "o/r", items, 40, 0, false));
        var page = Scroller(tab);
        var window = Show(page, 800, 400);
        var header = Named(window, GitHubPage.HeaderName)!;
        Require(page.Scroll.Extent.Height > page.Scroll.Viewport.Height, "The list is longer than its viewport");
        Require(header.Bounds.Height > 0 && header.Bounds.Top == 0, "The header sits at the top");
        page.Scroll.Offset = new Vector(0, page.Scroll.Extent.Height); Frame(window);
        Require(page.Scroll.Offset.Y > 0 && header.TranslatePoint(new Point(0, 0), window)!.Value.Y == 0, "The header stays at the top after the list scrolls to the end");
        window.Close();
        Console.WriteLine("PASS GitHub list header stays pinned while the list scrolls");
    }

    /// <summary>Every type spread at the top of a timeline query must be a pinned member of that kind's GitHub union.</summary>
    private static void RequireTimelineSpreads()
    {
        static List<string> Spreads(string events)
        {
            var found = new List<string>();
            var depth = 0;
            for (var i = 0; i < events.Length; i++)
            {
                if (events[i] == '{') depth++;
                else if (events[i] == '}') depth--;
                else if (depth == 0 && string.CompareOrdinal(events, i, "... on ", 0, 7) == 0)
                {
                    var start = i + 7;
                    var end = start;
                    while (char.IsLetterOrDigit(events[end])) end++;
                    found.Add(events[start..end]);
                }
            }
            return found;
        }

        var issue = Spreads(GitHubBrowser.IssueEvents);
        var pull = Spreads(GitHubBrowser.SharedEvents + GitHubBrowser.PullEvents);
        Require(issue.Count > 5 && pull.Count > issue.Count, "The timeline queries spread their events");
        var wrongIssue = issue.Where(t => !GitHubTimelineSchema.IssueTimelineItems.Contains(t)).ToList();
        var wrongPull = pull.Where(t => !GitHubTimelineSchema.PullRequestTimelineItems.Contains(t)).ToList();
        Require(wrongIssue.Count == 0, "The issue timeline spreads only IssueTimelineItems members, not " + string.Join(", ", wrongIssue));
        Require(wrongPull.Count == 0, "The pull request timeline spreads only PullRequestTimelineItems members, not " + string.Join(", ", wrongPull));
        Require(GitHubBrowser.IssueTimelineQuery.Contains(GitHubBrowser.IssueEvents, StringComparison.Ordinal) && GitHubBrowser.PullTimelineQuery.Contains(GitHubBrowser.PullEvents, StringComparison.Ordinal), "Each kind queries its own event list");
    }

    public static async Task Run()
    {
        var calls = new List<string>();
        string? comment = "first";
        var failing = false;
        var detailCalls = 0;
        var timelineFails = false;
        var posting = false;
        var jobDone = false;
        var browser = new GitHubBrowser((tool, directory, args, timeout, environment, ct) =>
        {
            calls.Add(string.Join(' ', args));
            var text = string.Join('\n', args);
            if (failing) return ValueTask.FromResult(new RepoHostToolResult(1, "", "HTTP 401: bad credentials"));
            if (args[0] == "repo") return ValueTask.FromResult(new RepoHostToolResult(0, Repo, ""));
            if (text.Contains("list: search", StringComparison.Ordinal)) return ValueTask.FromResult(new RepoHostToolResult(0, ListJson(), ""));
            if (text.Contains("issueOrPullRequest", StringComparison.Ordinal))
            {
                var timeline = text.Contains("timelineItems", StringComparison.Ordinal);
                if (timeline && timelineFails) return ValueTask.FromResult(new RepoHostToolResult(1, "", "gh: Fragment on ReviewRequestedEvent can't be spread inside IssueTimelineItems"));
                return ValueTask.FromResult(new RepoHostToolResult(0, DetailJson(timeline && detailCalls++ == 0 && comment == "first", comment ?? ""), ""));
            }
            static RepoHostToolResult Ok(string json) => new(0, json, "");
            if (args[0] == "api" && args[1].StartsWith("repos/o/r/actions/runs/9/jobs", StringComparison.Ordinal))
                return ValueTask.FromResult(Ok("""{"jobs":[{"id":1,"name":"build","status":"completed","conclusion":"failure","started_at":"2026-11-01T10:00:00Z","completed_at":"2026-11-01T10:02:05Z","html_url":"https://github.com/o/r/actions/runs/9/job/1","steps":[{"number":1,"name":"Checkout","status":"completed","conclusion":"success","started_at":"2026-11-01T10:00:00Z","completed_at":"2026-11-01T10:00:03Z"}]}]}"""));
            if (args[0] == "api" && args[1].StartsWith("repos/o/r/actions/runs/9", StringComparison.Ordinal))
                return ValueTask.FromResult(Ok("""{"id":9,"name":"CI","display_title":"Fix it","run_number":4,"event":"push","status":"in_progress","head_branch":"main","head_sha":"abcdef1234","actor":{"login":"ann"},"created_at":"2026-11-01T10:00:00Z","html_url":"https://github.com/o/r/actions/runs/9"}"""));
            if (args[0] == "api" && args[1].StartsWith("repos/o/r/actions/runs?", StringComparison.Ordinal))
                return ValueTask.FromResult(Ok("""{"total_count":2500,"workflow_runs":[{"id":9,"name":"CI","display_title":"Fix it","run_number":4,"event":"push","status":"completed","conclusion":"failure","head_branch":"main","head_sha":"abcdef1234","actor":{"login":"ann"},"run_started_at":"2026-11-01T10:00:00Z","created_at":"2026-11-01T10:00:00Z","updated_at":"2026-11-01T10:01:04Z","html_url":"https://github.com/o/r/actions/runs/9"}]}"""));
            if (args[0] == "api" && args[1].StartsWith("repos/o/r/releases", StringComparison.Ordinal))
                return ValueTask.FromResult(Ok("""[{"tag_name":"v2","name":"Two","draft":false,"prerelease":false,"published_at":"2026-11-01T10:00:00Z","author":{"login":"ann"},"body":"## Notes","html_url":"https://github.com/o/r/releases/tag/v2","assets":[{"name":"a.zip","size":2097152}]}]"""));
            if (args[0] == "api" && args[1].StartsWith("repos/o/r/contributors", StringComparison.Ordinal))
                return ValueTask.FromResult(Ok("""[{"login":"ann","contributions":12,"type":"User","html_url":"https://github.com/ann"}]"""));
            if (args[0] == "api" && args[1].StartsWith("orgs/o/packages", StringComparison.Ordinal))
                return ValueTask.FromResult(new RepoHostToolResult(1, "", "gh: You need at least read:packages scope to list packages. (HTTP 403)"));
            if (args[0] == "api" && args.Contains("repos/o/r/issues/7/comments"))
                return ValueTask.FromResult(posting ? new RepoHostToolResult(1, "", "gh: Resource not accessible by personal access token (HTTP 403)") : Ok("""{"id":555,"user":{"login":"me"},"created_at":"2026-11-02T10:00:00Z","body":"Thanks"}"""));
            if (args[0] == "api" && args.Contains("repos/o/r/issues/7") && args.Contains("PATCH")) return ValueTask.FromResult(Ok("""{"state":"closed"}"""));
            if (text.Contains("mutation(", StringComparison.Ordinal)) return ValueTask.FromResult(Ok("""{"data":{"addDiscussionComment":{"comment":{"databaseId":901,"createdAt":"2026-11-02T10:00:00Z","body":"Hi","author":{"login":"me"}}}}}"""));
            if (text.Contains("discussion(number: $number) { id }", StringComparison.Ordinal)) return ValueTask.FromResult(Ok("""{"data":{"repository":{"discussion":{"id":"D_1"}}}}"""));
            if (args[0] == "api" && args[1] == "repos/o/r/actions/jobs/5") return ValueTask.FromResult(Ok(jobDone ? """{"status":"completed","html_url":"https://github.com/o/r/actions/runs/9/job/5"}""" : """{"status":"in_progress","html_url":"https://github.com/o/r/actions/runs/9/job/5"}"""));
            if (args[0] == "api" && args[1] == "repos/o/r/actions/jobs/5/logs") return ValueTask.FromResult(Ok(JobLog));
            if (text.Contains("discussionCategories", StringComparison.Ordinal))
                return ValueTask.FromResult(Ok("""{"data":{"repository":{"discussionCategories":{"nodes":[{"id":"C1","name":"Q&A","emoji":":pray:"}]}}}}"""));
            if (text.Contains("discussions(first", StringComparison.Ordinal))
                return ValueTask.FromResult(Ok("""{"data":{"repository":{"discussions":{"totalCount":1,"pageInfo":{"hasNextPage":false},"nodes":[{"number":3,"title":"How?","url":"u","createdAt":"2026-11-01T10:00:00Z","author":{"login":"bob"},"category":{"name":"Q&A","emoji":":pray:"},"comments":{"totalCount":2},"answer":{"id":"A"},"closed":false,"upvoteCount":1}]}}}}"""));
            if (text.Contains("discussion(number", StringComparison.Ordinal))
                return ValueTask.FromResult(Ok("""{"data":{"repository":{"discussion":{"number":3,"title":"How?","body":"Q","url":"u","createdAt":"2026-11-01T10:00:00Z","author":{"login":"bob"},"category":{"name":"Q&A","emoji":":pray:"},"closed":false,"answer":{"id":"A"},"comments":{"nodes":[{"id":"A","author":{"login":"ann"},"createdAt":"2026-11-01T11:00:00Z","body":"Like this","replies":{"nodes":[{"author":{"login":"bob"},"createdAt":"2026-11-01T12:00:00Z","body":"Thanks"}]}}]}}}}}"""));
            if (text.Contains("deployments(first", StringComparison.Ordinal))
                return ValueTask.FromResult(Ok("""{"data":{"repository":{"deployments":{"totalCount":1,"pageInfo":{"hasNextPage":false},"nodes":[{"id":"D1","environment":"production","ref":{"name":"main"},"commit":{"abbreviatedOid":"abc1234"},"creator":{"login":"ann"},"createdAt":"2026-11-01T10:00:00Z","latestStatus":{"state":"SUCCESS"}}]}}}}"""));
            return ValueTask.FromResult(new RepoHostToolResult(1, "", "unexpected"));
        });

        var list = await browser.ListAsync("/w", new("w", "pulls", "is:closed label:bug"), default);
        Require(list.Problem is null && list.Repo == "o/r" && list.Items.Count == 1 && list.HasMore, "A list reads the repository and its rows");
        Require(list is { Open: 4, Closed: 9 }, "A list carries the open and closed counts");
        var row = list.Items[0];
        Require(row is { Number: 7, State: "merged", Author: "ann", Comments: 3, Checks: "passing", Review: "approved" } && row.Labels[0].Color == "d73a4a", "A pull request row carries state, checks and review");
        var search = calls.First(call => call.Contains("list: search", StringComparison.Ordinal));
        Require(search.Contains("q=repo:o/r is:pr is:closed label:bug sort:created-desc", StringComparison.Ordinal), "The user's query is passed in GitHub's syntax after the repository and kind");
        Require(search.Contains("open=repo:o/r is:pr label:bug is:open", StringComparison.Ordinal) && search.Contains("closed=repo:o/r is:pr label:bug is:closed", StringComparison.Ordinal),
            "The counts drop the state qualifier and ask for each state");
        var again = await browser.ListAsync("/w", new("w", "pulls", "is:closed label:bug", 25, list.Revision), default);
        Require(again.Unchanged && again.Items.Count == 0 && again.Revision == list.Revision, "An unchanged list answers Unchanged without rows");

        var detail = await browser.DetailAsync("/w", new("w", "pulls", 7), default);
        Require(detail.Problem is null && detail.Kind == "pulls" && detail.Draft && detail.Body == "Body text" && detail.Assignees.SequenceEqual(["bob"]), "A detail carries the header and body");
        Require(detail.Pull is { BaseRef: "main", Mergeability: "blocked", Review: "required", Checks: "failing" } && detail.Pull.CheckRuns.Count == 2, "A pull request carries branches, merge state and check runs");
        Require(detail.Timeline.Select(e => e.Kind).SequenceEqual(["commit", "comment", "review", "labeled", "commit", "comment", "review", "labeled"]), "Timeline pages are joined and unknown events are skipped");
        Require(detail.Timeline[2] is { Detail: "changes-requested", Comments: [{ Path: "b.txt" }] }, "A review keeps its inline comments");
        Require(!detail.Truncated, "Two pages are not truncated");
        var revision = detail.Revision;
        comment = "first";
        detailCalls = 0;
        var same = await browser.DetailAsync("/w", new("w", "pulls", 7, revision), default);
        Require(same.Unchanged && same.Timeline.Count == 0, "An unchanged conversation answers Unchanged");
        comment = "second";
        var moved = await browser.DetailAsync("/w", new("w", "pulls", 7, revision), default);
        Require(!moved.Unchanged && moved.Timeline.Any(e => e.Body == "second") && moved.Revision != revision, "A new comment changes the revision and comes back in full");
        Require(browser.CachedRevisions == 2, "Only revisions are cached");

        timelineFails = true;
        var halfLoaded = await browser.DetailAsync("/w", new("w", "pulls", 7), default);
        Require(halfLoaded.Problem is null && halfLoaded.Body == "Body text" && halfLoaded.Timeline.Count == 0 && halfLoaded.TimelineProblem!.Contains("ReviewRequestedEvent", StringComparison.Ordinal),
            "A timeline error keeps the header and body and reports the error");
        timelineFails = false;
        RequireTimelineSpreads();

        var runs = await browser.ViewAsync("/w", new("w", "runs", "", [new("status", "failure")]), default);
        Require(runs.Problem is null && runs.Rows[0] is { State: "failure", Right: "1m 4s" } && runs.Rows[0].Meta == "CI #4: Commit abcdef1 pushed by ann" && runs.Rows[0].Chips[0].Name == "main", "A run row carries state, wording, branch and duration");
        Require(runs.Summary == "2,500 workflow runs" && runs.Filters.Select(x => x.Key).SequenceEqual(["event", "status", "branch", "actor"]) && runs.Filters[1].Value == "failure", "Runs offer the event, status, branch and actor filters");
        Require(calls.Any(call => call.Contains("status=failure", StringComparison.Ordinal)), "A chosen status goes to the runs call");
        var runAgain = await browser.ViewAsync("/w", new("w", "runs", "", [new("status", "failure")], 30, runs.Revision), default);
        Require(runAgain.Unchanged && runAgain.Rows.Count == 0, "An unchanged runs list answers Unchanged");
        var run = await browser.ViewAsync("/w", new("w", "runs", "9", []), default);
        Require(run.State == "running" && run.Rows.Select(r => (r.Title, r.Indent)).SequenceEqual([("build", 0), ("Checkout", 1)]) && run.Rows[0].Right == "2m 5s", "A run lists its jobs with their steps");
        var discussions = await browser.ViewAsync("/w", new("w", "discussions", "", [new("category", "Q&A")]), default);
        Require(discussions.Rows[0] is { State: "answered" } && discussions.Filters[0].Options.SequenceEqual(["Q&A"]) && calls.Any(call => call.Contains("category=C1", StringComparison.Ordinal)), "Discussions filter by category id");
        var discussion = await browser.ViewAsync("/w", new("w", "discussions", "3", []), default);
        Require(discussion.Cards.Select(card => (card.Author, card.Badge, card.Note)).SequenceEqual([("bob", "", ""), ("ann", "Answer", ""), ("bob", "", "replied")]), "A discussion shows the question, the answer and replies");
        var releases = await browser.ViewAsync("/w", new("w", "releases", "", []), default);
        Require(releases.Cards[0] is { Badge: "Latest", Title: "Two" } && releases.Cards[0].Body.Contains("a.zip (2.0 MB)", StringComparison.Ordinal), "A release is a card with its notes and assets");
        var people = await browser.ViewAsync("/w", new("w", "contributors", "", []), default);
        Require(people.Rows[0] is { Title: "ann", Right: "12 commits" }, "Contributors list their commits");
        var deployments = await browser.ViewAsync("/w", new("w", "deployments", "", []), default);
        Require(deployments.Rows[0] is { Title: "production", State: "success" }, "A deployment shows its environment and latest status");
        var packages = await browser.ViewAsync("/w", new("w", "packages", "", []), default);
        Require(packages.Problem is not null && packages.Problem.Contains("read:packages", StringComparison.Ordinal), "A missing packages scope is reported, not guessed");

        var ok = await browser.PostAsync("/w", new("w", "pulls", 7, "comment", " Thanks "), default);
        Require(ok.Ok && ok.Event is { Id: "555", Author: "me", Body: "Thanks" } && calls.Last().Contains("body=Thanks", StringComparison.Ordinal), "A comment posts through gh api and comes back with its id");
        Require(!(await browser.PostAsync("/w", new("w", "pulls", 7, "comment", "   "), default)).Ok, "An empty comment is refused before any call");
        var closed = await browser.PostAsync("/w", new("w", "issues", 7, "close", "Done"), default);
        Require(closed is { Ok: true, State: "closed" } && calls.Any(call => call.Contains("-X PATCH repos/o/r/issues/7 -f state=closed", StringComparison.Ordinal)), "Close posts the comment first, then sets the state");
        posting = true;
        var denied = await browser.PostAsync("/w", new("w", "pulls", 7, "comment", "Hi"), default);
        Require(!denied.Ok && denied.Event is null && denied.Problem!.Contains("gh auth refresh -s repo", StringComparison.Ordinal), "A missing write scope names the fix");
        posting = false;
        var reply = await browser.PostAsync("/w", new("w", "discussions", 3, "comment", "Hi"), default);
        Require(reply.Ok && reply.Event is { Id: "901" }, "A discussion comment goes through the GraphQL mutation");
        var timeline = new GitHubEvent[] { new("comment", "me", "t", "Thanks", "") { Id = "555" } };
        Require(GitHubPending.Remaining([ok.Event!], timeline).Count == 0 && GitHubPending.Remaining([ok.Event!], []).Count == 1, "A posted comment is shown until the poll returns it, then once");
        GitHubDrafts.Set("w|pulls|7", "half a thought");
        Require(GitHubDrafts.Get("w|pulls|7") == "half a thought" && GitHubDrafts.Get("w|pulls|8") == "", "A draft is kept per tab across rebuilds");
        Require(GitHubLinks.Parse("https://github.com/o/r/pull/12#issuecomment-1", "o/r") is ("pull:12", _) && GitHubLinks.Parse("https://github.com/o/r/actions/runs/99", "o/r") is ("run:99", _)
            && GitHubLinks.Parse("https://github.com/o/r/discussions/4", "o/r") is ("discussion:4", _) && GitHubLinks.Parse("https://github.com/o/r/releases/tag/v1", "o/r") is ("releases", _), "Links to this repository map to tabs");
        Require(GitHubLinks.Parse("https://github.com/x/y/issues/1", "o/r") is null && GitHubLinks.Parse("http://github.com/o/r/issues/1", "o/r") is null && GitHubLinks.Parse("javascript:alert(1)", "o/r") is null, "Other repositories, http and other schemes are not ours to open");
        Require(GitHubLinks.Rewrite("See #12 and `#13` but not a#14\n```\n#15\n```", "o/r") == "See [#12](https://github.com/o/r/issues/12) and `#13` but not a#14\n```\n#15\n```", "Bare #N becomes a link outside code");
        var running = await browser.LogAsync("/w", new("w", "5"), default);
        Require(running is { InProgress: true } && running.Problem!.Contains("still running", StringComparison.Ordinal), "A running job says its log is not ready");
        jobDone = true;
        var log = await browser.LogAsync("/w", new("w", "5"), default);
        Require(log.Problem is null && log.Groups[0].Name == "Run build" && log.Groups[0].HasError && log.Groups[0].Lines[0] == new GitHubLogLine("10:00:01", "make") && log.Groups[0].Lines[1].Text == "Process completed with exit code 2.", "A log is split into groups with stamps and colour codes removed");
        var before = calls.Count;
        await browser.LogAsync("/w", new("w", "5"), default);
        Require(calls.Count == before, "A finished job's log is cached");
        var huge = GitHubBrowser.ParseLog(string.Join('\n', Enumerable.Range(0, 20000).Select(i => "2026-11-01T10:00:00.0000000Z line " + i)), "");
        Require(huge.Groups.Sum(g => g.Lines.Count) <= 500 && huge.Cut == 19500 && huge.Groups[0].Lines[^1].Text == "line 19999", "A huge log keeps its tail and counts what it cut");
        var many = GitHubBrowser.ParseLog(string.Join('\n', Enumerable.Range(0, 20).SelectMany(g => new[] { "##[group]G" + g }.Concat(Enumerable.Range(0, 400).Select(i => "x")))), "");
        Require(many.Groups.Sum(g => g.Lines.Count) <= 4000 && many.Cut == 4000, "Many groups stay under the total cap");

        failing = true;
        var broken = await browser.ListAsync("/w", new("w", "issues", ""), default);
        Require(broken.Problem == "HTTP 401: bad credentials" || broken.Problem is not null, "A gh failure is a problem, not an exception");
        Console.WriteLine("PASS GitHub issue and pull request browsing through canned gh output");
    }
}