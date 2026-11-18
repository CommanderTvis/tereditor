using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tereditor.Plugins.GitHub.Host;

/// <summary>
/// Read-only GitHub browsing through <c>gh api graphql</c>: issue and pull request lists in GitHub's own search syntax,
/// and a conversation with its timeline. It keeps only a revision (a hash of the last answer) per view, so a caller that
/// polls gets <c>Unchanged</c> instead of the same payload again.
/// </summary>
public sealed partial class GitHubBrowser(ToolRunner run)
{
    private static readonly TimeSpan Query = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RepoTtl = TimeSpan.FromMinutes(10);
    private const int TimelinePages = 5;
    private static readonly Dictionary<string, string?> GhEnvironment = new() { ["GH_PROMPT_DISABLED"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["NO_COLOR"] = "1" };

    private readonly ConcurrentDictionary<string, string> revisions = new();
    private readonly ConcurrentDictionary<string, (RepoFacts Facts, long At)> repos = new();

    internal sealed record RepoFacts(string Owner, string Name, string Url, bool Discussions)
    {
        public string Slug => Owner + "/" + Name;
    }

    /// <summary>How many views the host remembers a revision for.</summary>
    public int CachedRevisions => revisions.Count;

    private static bool Offline => Environment.GetEnvironmentVariable("TEREDITOR_GH_OFFLINE") == "1";

    public async Task<GitHubList> ListAsync(string directory, GitHubListParams p, CancellationToken ct)
    {
        try
        {
            if (Offline) return Empty("", "GitHub is switched off for this session.");
            var repo = await RepoAsync(directory, ct);
            var kind = p.Kind == "pulls" ? "pr" : "issue";
            var user = p.Query.Trim();
            var baseQuery = $"repo:{repo.Slug} is:{kind} {StateQualifiers().Replace(user, "").Trim()}".Trim();
            var full = $"repo:{repo.Slug} is:{kind} {(user.Length == 0 ? "is:open" : user)}" + (user.Contains("sort:", StringComparison.Ordinal) ? "" : " sort:created-desc");
            var limit = Math.Clamp(p.Limit, 1, 100);
            using var json = await GraphQlAsync(directory, ListQuery, ct,
                ("q", full), ("open", baseQuery + " is:open"), ("closed", baseQuery + " is:closed"), ("first", limit), ("owner", repo.Owner), ("name", repo.Name));
            var data = json.RootElement.GetProperty("data");
            var list = data.GetProperty("list");
            var items = list.GetProperty("nodes").EnumerateArray().Where(node => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("number", out _))
                .Select(Item).ToList();
            var key = $"list|{repo.Slug}|{kind}|{full}|{limit}";
            var result = new GitHubList(false, "", repo.Slug, items, data.GetProperty("open").GetProperty("issueCount").GetInt32(),
                data.GetProperty("closed").GetProperty("issueCount").GetInt32(), list.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
            {
                RepoLabels = data.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object ? Labels(labels) : []
            };
            var revision = Revise(key, JsonSerializer.Serialize(result, GitHubJson.Default.GitHubList));
            return p.Revision == revision ? result with { Unchanged = true, Revision = revision, Items = [] } : result with { Revision = revision };
        }
        catch (Exception error) when (IsExpected(error)) { return Empty("", Describe(error)); }
    }

    public async Task<GitHubDetail> DetailAsync(string directory, GitHubDetailParams p, CancellationToken ct)
    {
        try
        {
            if (Offline) return Failed(p, "GitHub is switched off for this session.");
            var repo = await RepoAsync(directory, ct);
            var nodes = new List<JsonElement>();
            var documents = new List<JsonDocument>();
            string? after = null;
            var truncated = false;
            JsonElement head = default;
            try
            {
                for (var page = 0; page < TimelinePages; page++)
                {
                    var json = await GraphQlAsync(directory, DetailQuery, ct, ("owner", repo.Owner), ("name", repo.Name), ("number", p.Number), ("after", after ?? ""));
                    documents.Add(json);
                    var node = json.RootElement.GetProperty("data").GetProperty("repository").GetProperty("issueOrPullRequest");
                    if (node.ValueKind != JsonValueKind.Object) return Failed(p, "That number is not an issue or pull request here.");
                    if (page == 0) head = node;
                    var timeline = node.GetProperty("timelineItems");
                    nodes.AddRange(timeline.GetProperty("nodes").EnumerateArray());
                    var info = timeline.GetProperty("pageInfo");
                    if (!info.GetProperty("hasNextPage").GetBoolean()) break;
                    after = info.GetProperty("endCursor").GetString();
                    truncated = page == TimelinePages - 1;
                }
                var pull = Text(head, "__typename") == "PullRequest";
                var author = Text(head.GetProperty("author"), "login");
                var detail = new GitHubDetail(false, "", repo.Slug, pull ? "pulls" : "issues", p.Number, Text(head, "title"), State(head), pull && Bool(head, "isDraft"), author,
                    Text(head, "createdAt"), Text(head, "url"), Text(head, "body"), Labels(head), Names(head, "assignees", "login"),
                    head.TryGetProperty("milestone", out var milestone) && milestone.ValueKind == JsonValueKind.Object ? Text(milestone, "title") : null,
                    [.. nodes.Select(Event).OfType<GitHubEvent>()], truncated, pull ? Pull(head) : null);
                var revision = Revise($"detail|{repo.Slug}|{p.Number}", JsonSerializer.Serialize(detail, GitHubJson.Default.GitHubDetail));
                return p.Revision == revision ? detail with { Unchanged = true, Revision = revision, Body = "", Timeline = [] } : detail with { Revision = revision };
            }
            finally { foreach (var document in documents) document.Dispose(); }
        }
        catch (Exception error) when (IsExpected(error)) { return Failed(p, Describe(error)); }
    }

    public async Task<GitHubInfo> InfoAsync(string directory, int pollSeconds, CancellationToken ct)
    {
        var account = "";
        var signedIn = false;
        try
        {
            var auth = await run("gh", directory, ["auth", "status"], TimeSpan.FromSeconds(10), GhEnvironment, ct);
            account = (auth.Output + auth.Error).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            signedIn = auth.ExitCode == 0;
            if (!signedIn) account = "gh is installed but not signed in. Run gh auth login.";
        }
        catch (Win32Exception) { return new(false, "The gh command-line tool was not found on PATH.", "", "", false, pollSeconds, CachedRevisions); }
        catch (TimeoutException) { return new(false, "gh did not respond.", "", "", false, pollSeconds, CachedRevisions); }
        if (!signedIn) return new(false, account, "", "", false, pollSeconds, CachedRevisions);
        try
        {
            var repo = await RepoAsync(directory, ct);
            return new(true, account, repo.Slug, repo.Url, repo.Discussions, pollSeconds, CachedRevisions);
        }
        catch (Exception error) when (IsExpected(error)) { return new(true, account, "", "", false, pollSeconds, CachedRevisions, Describe(error)); }
    }

    private async Task<RepoFacts> RepoAsync(string directory, CancellationToken ct)
    {
        if (repos.TryGetValue(directory, out var cached) && Environment.TickCount64 - cached.At < RepoTtl.TotalMilliseconds) return cached.Facts;
        var result = await run("gh", directory, ["repo", "view", "--json", "owner,name,url,hasDiscussionsEnabled"], Query, GhEnvironment, ct);
        if (result.ExitCode != 0) throw new IOException(Trim(result.Error, "This folder has no GitHub repository, or gh is not signed in."));
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        var facts = new RepoFacts(Text(root.GetProperty("owner"), "login"), Text(root, "name"), Text(root, "url"), Bool(root, "hasDiscussionsEnabled"));
        repos[directory] = (facts, Environment.TickCount64);
        return facts;
    }

    private async Task<JsonDocument> GraphQlAsync(string directory, string query, CancellationToken ct, params (string Name, object Value)[] variables)
    {
        var args = new List<string> { "api", "graphql", "-f", "query=" + query };
        foreach (var (name, value) in variables)
        {
            if (value is int number) { args.Add("-F"); args.Add(name + "=" + number.ToString(CultureInfo.InvariantCulture)); }
            else if (value is string text && text.Length == 0 && name == "after") continue;
            else { args.Add("-f"); args.Add(name + "=" + value); }
        }
        var result = await run("gh", directory, args, Query, GhEnvironment, ct);
        if (result.ExitCode != 0) throw new IOException(Trim(result.Error, "gh could not read GitHub."));
        var json = JsonDocument.Parse(result.Output);
        if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
            && !(json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object))
        {
            var message = Text(errors[0], "message");
            json.Dispose();
            throw new IOException(message.Length == 0 ? "GitHub rejected the query." : message);
        }
        return json;
    }

    private string Revise(string key, string content)
    {
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
        revisions[key] = revision;
        return revision;
    }

    private static GitHubItem Item(JsonElement node)
    {
        var pull = Text(node, "__typename") == "PullRequest";
        return new(node.GetProperty("number").GetInt32(), Text(node, "title"), State(node), pull && Bool(node, "isDraft"), Author(node), Labels(node),
            node.TryGetProperty("comments", out var comments) ? comments.GetProperty("totalCount").GetInt32() : 0,
            Text(node, "createdAt"), Text(node, "updatedAt"), node.TryGetProperty("closedAt", out var closed) && closed.ValueKind == JsonValueKind.String ? closed.GetString() : null,
            Text(node, "url"), pull ? RollupState(node) : "none", pull ? ReviewState(node) : "none");
    }

    private static GitHubPullFacts Pull(JsonElement node)
    {
        var runs = new List<GitHubCheck>();
        var rollup = Rollup(node);
        if (rollup.ValueKind == JsonValueKind.Object && rollup.TryGetProperty("contexts", out var contexts))
            foreach (var context in contexts.GetProperty("nodes").EnumerateArray())
            {
                if (Text(context, "__typename") == "CheckRun") runs.Add(new(Text(context, "name"), CheckState(Text(context, "conclusion"), Text(context, "status"))));
                else runs.Add(new(Text(context, "context"), Text(context, "state") switch { "SUCCESS" => "passing", "PENDING" or "EXPECTED" => "pending", _ => "failing" }));
            }
        return new(Text(node, "baseRefName"), Text(node, "headRefName"), node.GetProperty("additions").GetInt32(), node.GetProperty("deletions").GetInt32(),
            node.GetProperty("changedFiles").GetInt32(), ReviewState(node),
            Text(node, "mergeStateStatus") switch { "CLEAN" or "UNSTABLE" or "HAS_HOOKS" => "clean", "BLOCKED" => "blocked", "BEHIND" => "behind", "DIRTY" => "conflicting", _ => "unknown" },
            RollupState(node), runs);
    }

    private static JsonElement Rollup(JsonElement node) =>
        node.TryGetProperty("commits", out var commits) && commits.TryGetProperty("nodes", out var list) && list.GetArrayLength() > 0
        && list[0].GetProperty("commit").TryGetProperty("statusCheckRollup", out var rollup) ? rollup : default;

    private static string RollupState(JsonElement node) => Rollup(node) is { ValueKind: JsonValueKind.Object } rollup
        ? Text(rollup, "state") switch { "SUCCESS" => "passing", "FAILURE" or "ERROR" => "failing", "PENDING" or "EXPECTED" => "pending", _ => "none" }
        : "none";

    private static string ReviewState(JsonElement node) => Text(node, "reviewDecision") switch
    {
        "APPROVED" => "approved", "CHANGES_REQUESTED" => "changes-requested", "REVIEW_REQUIRED" => "required", _ => "none"
    };

    private static string CheckState(string conclusion, string status) =>
        conclusion is "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE" ? "failing"
        : status.Length > 0 && status != "COMPLETED" ? "pending" : "passing";

    private static GitHubEvent? Event(JsonElement node)
    {
        var at = Text(node, "createdAt");
        var actor = node.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.Object ? Text(a, "login") : "";
        GitHubEvent Simple(string kind, string detail = "", string color = "") => new(kind, actor, at, "", detail, color);
        switch (Text(node, "__typename"))
        {
            case "IssueComment": return new("comment", Author(node), at, Text(node, "body"), Text(node, "authorAssociation"));
            case "LabeledEvent": return Simple("labeled", Text(node.GetProperty("label"), "name"), Text(node.GetProperty("label"), "color"));
            case "UnlabeledEvent": return Simple("unlabeled", Text(node.GetProperty("label"), "name"), Text(node.GetProperty("label"), "color"));
            case "ClosedEvent": return Simple("closed");
            case "ReopenedEvent": return Simple("reopened");
            case "AssignedEvent": return Simple("assigned", Login(node, "assignee"));
            case "UnassignedEvent": return Simple("unassigned", Login(node, "assignee"));
            case "RenamedTitleEvent": return Simple("renamed", Text(node, "previousTitle") + " to " + Text(node, "currentTitle"));
            case "MilestonedEvent": return Simple("milestoned", Text(node, "milestoneTitle"));
            case "CrossReferencedEvent":
                var source = node.GetProperty("source");
                return Simple("referenced", source.ValueKind == JsonValueKind.Object ? "#" + source.GetProperty("number").GetInt32() + " " + Text(source, "title") : "");
            case "ReviewRequestedEvent": return Simple("review-requested", Login(node, "requestedReviewer"));
            case "MergedEvent": return Simple("merged", Text(node, "mergeRefName"));
            case "ReadyForReviewEvent": return Simple("ready");
            case "ConvertToDraftEvent": return Simple("draft");
            case "HeadRefForcePushedEvent": return Simple("force-pushed");
            case "HeadRefDeletedEvent": return Simple("branch-deleted", Text(node, "headRefName"));
            case "PullRequestReview":
                var comments = node.GetProperty("comments").GetProperty("nodes").EnumerateArray().Select(c => new GitHubReviewComment(Text(c, "path"), Text(c, "body"))).ToList();
                return new("review", Author(node), at, Text(node, "body"), Text(node, "state") switch
                {
                    "APPROVED" => "approved", "CHANGES_REQUESTED" => "changes-requested", "DISMISSED" => "dismissed", _ => "commented"
                }, "", comments);
            case "PullRequestCommit":
                var commit = node.GetProperty("commit");
                var who = commit.GetProperty("author").TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? Text(user, "login") : Text(commit.GetProperty("author"), "name");
                return new("commit", who, Text(commit, "authoredDate"), Text(commit, "messageHeadline"), Text(commit, "oid") is { Length: >= 7 } oid ? oid[..7] : "");
            default: return null;
        }
    }

    private static string Login(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? Text(value, "login") : "";

    private static string Author(JsonElement node) =>
        node.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object ? Text(author, "login") : "ghost";

    private static string State(JsonElement node) => Text(node, "state") switch { "OPEN" => "open", "MERGED" => "merged", _ => "closed" };

    private static IReadOnlyList<GitHubLabel> Labels(JsonElement node) =>
        node.TryGetProperty("labels", out var labels) ? [.. labels.GetProperty("nodes").EnumerateArray().Select(l => new GitHubLabel(Text(l, "name"), Text(l, "color")))] : [];

    private static IReadOnlyList<string> Names(JsonElement node, string list, string property) =>
        node.TryGetProperty(list, out var items) ? [.. items.GetProperty("nodes").EnumerateArray().Select(i => Text(i, property))] : [];

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Trim(string error, string fallback) => error.Trim().Length == 0 ? fallback : error.Trim().Split('\n')[0];

    private static bool IsExpected(Exception error) => error is IOException or TimeoutException or Win32Exception or JsonException or KeyNotFoundException or InvalidOperationException;

    private static string Describe(Exception error) => error switch
    {
        Win32Exception => "The gh command-line tool was not found on PATH.",
        TimeoutException => "GitHub did not answer in time.",
        JsonException or KeyNotFoundException or InvalidOperationException => "GitHub answered in a form this version does not read.",
        _ => error.Message
    };

    private static GitHubList Empty(string repo, string problem) => new(false, "", repo, [], 0, 0, false, problem);

    private static GitHubDetail Failed(GitHubDetailParams p, string problem) =>
        new(false, "", "", p.Kind, p.Number, "", "open", false, "", "", "", "", [], [], null, [], false, null, problem);

    [GeneratedRegex(@"(^|\s)-?(is|state):(open|closed|merged)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex StateQualifiers();

    private const string Common = "number title state url createdAt updatedAt closedAt author { login } labels(first: 10) { nodes { name color } } comments { totalCount }";
    private const string PullList = "isDraft reviewDecision commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }";

    private const string ListQuery = "query($q: String!, $open: String!, $closed: String!, $first: Int!, $owner: String!, $name: String!) { "
        + "list: search(query: $q, type: ISSUE, first: $first) { pageInfo { hasNextPage } nodes { __typename ... on Issue { " + Common + " } ... on PullRequest { " + Common + " " + PullList + " } } } "
        + "labels: repository(owner: $owner, name: $name) { labels(first: 100) { nodes { name color } } } "
        + "open: search(query: $open, type: ISSUE) { issueCount } closed: search(query: $closed, type: ISSUE) { issueCount } }";

    private const string Timeline = "timelineItems(first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { __typename "
        + "... on IssueComment { author { login } createdAt body authorAssociation } "
        + "... on LabeledEvent { actor { login } createdAt label { name color } } ... on UnlabeledEvent { actor { login } createdAt label { name color } } "
        + "... on ClosedEvent { actor { login } createdAt } ... on ReopenedEvent { actor { login } createdAt } "
        + "... on AssignedEvent { actor { login } createdAt assignee { ... on User { login } } } ... on UnassignedEvent { actor { login } createdAt assignee { ... on User { login } } } "
        + "... on RenamedTitleEvent { actor { login } createdAt previousTitle currentTitle } ... on MilestonedEvent { actor { login } createdAt milestoneTitle } "
        + "... on CrossReferencedEvent { actor { login } createdAt source { ... on Issue { number title } ... on PullRequest { number title } } } "
        + "... on ReviewRequestedEvent { actor { login } createdAt requestedReviewer { ... on User { login } } } "
        + "... on MergedEvent { actor { login } createdAt mergeRefName } ... on ReadyForReviewEvent { actor { login } createdAt } ... on ConvertToDraftEvent { actor { login } createdAt } "
        + "... on HeadRefForcePushedEvent { actor { login } createdAt } ... on HeadRefDeletedEvent { actor { login } createdAt headRefName } "
        + "... on PullRequestReview { author { login } createdAt state body comments(first: 50) { nodes { path body } } } "
        + "... on PullRequestCommit { commit { oid messageHeadline authoredDate author { name user { login } } } } } }";

    private const string DetailQuery = "query($owner: String!, $name: String!, $number: Int!, $after: String) { repository(owner: $owner, name: $name) { issueOrPullRequest(number: $number) { __typename "
        + "... on Issue { " + Common + " body assignees(first: 10) { nodes { login } } milestone { title } " + Timeline + " } "
        + "... on PullRequest { " + Common + " " + PullList + " body assignees(first: 10) { nodes { login } } milestone { title } baseRefName headRefName additions deletions changedFiles mergeStateStatus "
        + "commits(last: 1) { nodes { commit { statusCheckRollup { state contexts(first: 50) { nodes { __typename ... on CheckRun { name conclusion status } ... on StatusContext { context state } } } } } } } " + Timeline + " } } } }";
}
