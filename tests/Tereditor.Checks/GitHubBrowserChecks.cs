using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.Host;

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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task Run()
    {
        var calls = new List<string>();
        string? comment = "first";
        var failing = false;
        var detailCalls = 0;
        var browser = new GitHubBrowser((tool, directory, args, timeout, environment, ct) =>
        {
            calls.Add(string.Join(' ', args));
            var text = string.Join('\n', args);
            if (failing) return ValueTask.FromResult(new RepoHostToolResult(1, "", "HTTP 401: bad credentials"));
            if (args[0] == "repo") return ValueTask.FromResult(new RepoHostToolResult(0, Repo, ""));
            if (text.Contains("list: search", StringComparison.Ordinal)) return ValueTask.FromResult(new RepoHostToolResult(0, ListJson(), ""));
            if (text.Contains("issueOrPullRequest", StringComparison.Ordinal))
                return ValueTask.FromResult(new RepoHostToolResult(0, DetailJson(detailCalls++ == 0 && comment == "first", comment ?? ""), ""));
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

        failing = true;
        var broken = await browser.ListAsync("/w", new("w", "issues", ""), default);
        Require(broken.Problem == "HTTP 401: bad credentials" || broken.Problem is not null, "A gh failure is a problem, not an exception");
        Console.WriteLine("PASS GitHub issue and pull request browsing through canned gh output");
    }
}
