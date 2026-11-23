using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.GitHub;

public sealed record GitHubSettings
{
    /// <summary>Seconds between refreshes of an open GitHub tab; 15 to 600.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Keep a local copy of GitHub answers so a tab opens with the last data at once.</summary>
    public bool KeepCache { get; set; } = true;
}

/// <summary>Set on an answer that came from the local copy. Refreshing: gh is being asked now. Reason: why gh could not be reached (the copy is all there is).</summary>
public sealed record GitHubCacheNote(bool Refreshing, string FetchedAt, string? Reason = null);

public sealed record GitHubSignInQuery;

/// <summary>Whether <c>gh</c> is signed in on the host, with the line to show.</summary>
public sealed record GitHubSignIn(bool Connected, string Detail);

/// <summary>One label with GitHub's own hex colour (no #).</summary>
public sealed record GitHubLabel(string Name, string Color);

/// <summary>One row of an issue or pull request list. State is open, closed or merged; Checks is passing, failing, pending or none.</summary>
/// <summary>A person with the address of their avatar image (http or https; may be empty).</summary>
public sealed record GitHubPerson(string Login, string AvatarUrl);

public sealed record GitHubItem(int Number, string Title, string State, bool Draft, string Author, IReadOnlyList<GitHubLabel> Labels, int Comments,
    string CreatedAt, string UpdatedAt, string? ClosedAt, string Url, string Checks, string Review)
{
    /// <summary>Who the item is assigned to.</summary>
    public IReadOnlyList<GitHubPerson> Assignees { get; set; } = [];
}

/// <summary>Kind is issues or pulls. Query is GitHub's search syntax without the repository and kind qualifiers. Revision is what the caller holds; the host answers Unchanged when it still matches.</summary>
public sealed record GitHubListParams(string WorkspaceId, string Kind, string Query, int Limit = 25, string? Revision = null);

public sealed record GitHubList(bool Unchanged, string Revision, string Repo, IReadOnlyList<GitHubItem> Items, int Open, int Closed, bool HasMore, string? Problem = null)
{
    /// <summary>The repository's labels, for the Label filter.</summary>
    public IReadOnlyList<GitHubLabel> RepoLabels { get; set; } = [];

    /// <summary>Not null when this answer is the saved copy.</summary>
    public GitHubCacheNote? Cached { get; init; }
}

/// <summary>One filter chip a view offers; Value is what is chosen now ("" for none).</summary>
public sealed record GitHubFilter(string Key, string Label, IReadOnlyList<string> Options, string Value = "");

public sealed record GitHubFilterValue(string Key, string Value);

/// <summary>One row of a generic view. State: open, closed, merged, success, failure, cancelled, skipped, running, queued, answered, none.</summary>
public sealed record GitHubRow(string Id, string Title, string State, string Meta, IReadOnlyList<GitHubLabel> Chips, string Right, string Url, string At = "", int Indent = 0);

/// <summary>A Markdown card: a release, a discussion post or a reply.</summary>
public sealed record GitHubCard(string Title, string Author, string At, string Note, string Body, string Badge = "", string Url = "")
{
    /// <summary>The comment's numeric id, when it is one; lets a comment posted from here be told apart from the one a refresh brings back.</summary>
    public string Id { get; set; } = "";
}

/// <summary>Kind: runs, discussions, releases, contributors, deployments or packages. Id is empty for the list, else the run id or discussion number.</summary>
public sealed record GitHubViewParams(string WorkspaceId, string Kind, string Id, IReadOnlyList<GitHubFilterValue> Filters, int Limit = 30, string? Revision = null)
{
    /// <summary>The 1-based page of Limit rows to read; a list view answers only that page.</summary>
    public int Page { get; set; } = 1;
}

/// <summary>One workflow of the repository. State is active or a disabled kind; Path is the file under .github/workflows (or dynamic/... for GitHub's own).</summary>
public sealed record GitHubWorkflow(string Id, string Name, string Path, string State);

/// <summary>One workflow run of the Actions list; light strings and numbers only. State as in <see cref="GitHubRow"/>. Started is ISO, Duration is ready text.</summary>
public sealed record GitHubRun(string Id, string Title, string State, string Workflow, string WorkflowId, string WorkflowPath, int Number, string Event, string Actor, string Sha,
    string Branch, string Started, string Duration, string Url);

public sealed record GitHubView(bool Unchanged, string Revision, string Title, string Subtitle, string State, IReadOnlyList<GitHubRow> Rows, IReadOnlyList<GitHubCard> Cards,
    IReadOnlyList<GitHubFilter> Filters, bool HasMore, string Summary = "", string? Problem = null)
{
    /// <summary>Not null when this answer is the saved copy.</summary>
    public GitHubCacheNote? Cached { get; init; }

    /// <summary>The runs of the asked page (Actions list only; Rows stays empty there).</summary>
    public IReadOnlyList<GitHubRun> Runs { get; set; } = [];

    /// <summary>The repository's workflows (Actions list, first page only).</summary>
    public IReadOnlyList<GitHubWorkflow> Workflows { get; set; } = [];

    /// <summary>The repository's web address, for workflow file links.</summary>
    public string RepoUrl { get; set; } = "";

    /// <summary>All runs that match, as GitHub counts them.</summary>
    public int Total { get; init; }
}

public sealed record GitHubContributorsParams(string WorkspaceId, string? Revision = null);

/// <summary>One author's weekly numbers, aligned with <see cref="GitHubContributors.Weeks"/>.</summary>
public sealed record GitHubContributor(string Login, string AvatarUrl, string Url, IReadOnlyList<int> Commits, IReadOnlyList<int> Additions, IReadOnlyList<int> Deletions);

/// <summary>GitHub's contributor statistics. Weeks are the Unix times of each week's start. Computing is true while GitHub still builds them (the caller asks again); an empty author list with no problem means GitHub has none to give.</summary>
public sealed record GitHubContributors(bool Unchanged, string Revision, string Branch, bool Computing, IReadOnlyList<long> Weeks, IReadOnlyList<GitHubContributor> Authors, string? Problem = null)
{
    /// <summary>The repository's web address, for the Open in browser link.</summary>
    public string RepoUrl { get; set; } = "";
}

public sealed record GitHubReviewComment(string Path, string Body);

/// <summary>One timeline entry. Kind: comment, review, commit, labeled, unlabeled, closed, reopened, merged, assigned, unassigned, renamed, milestoned, referenced, review-requested, ready, draft, force-pushed, branch-deleted.</summary>
public sealed record GitHubEvent(string Kind, string Author, string At, string Body, string Detail, string Color = "", IReadOnlyList<GitHubReviewComment>? Comments = null)
{
    /// <summary>The comment's numeric id (comments only).</summary>
    public string Id { get; set; } = "";
}

/// <summary>Action: comment, close or reopen (the last two take an optional comment first). Kind is issues, pulls or discussions.</summary>
public sealed record GitHubPostParams(string WorkspaceId, string Kind, int Number, string Action, string Body);

/// <summary>Ok is false when nothing was posted; Problem is gh's own text plus the fix for a missing scope. Event is the new comment, when one was made.</summary>
public sealed record GitHubPostResult(bool Ok, string? Problem, GitHubEvent? Event = null, string? State = null);

public sealed record GitHubLogParams(string WorkspaceId, string JobId);

/// <summary>One log line. Text keeps its colour codes (the view parses them per visible line). Header: it opens a ##[group]; Grouped: it sits inside one; Error: it was an ##[error] line.</summary>
public sealed record GitHubLogLine(string Time, string Text)
{
    public bool Header { get; init; }

    public bool Grouped { get; init; }

    public bool Error { get; init; }
}

/// <summary>The log lines of one step of a job (Number 0 and an empty name when the steps are unknown); Cut counts the lines left out of its start.</summary>
public sealed record GitHubLogStep(int Number, string Name, IReadOnlyList<GitHubLogLine> Lines, bool HasError, int Cut);

/// <summary>One job's log split into its steps; Cut counts the lines left out to keep it bounded; Url is the job's page.</summary>
public sealed record GitHubLog(IReadOnlyList<GitHubLogStep> Steps, int Cut, string Url, string? Problem = null, bool InProgress = false);

/// <summary>Draft comment text per tab, kept in memory so a refresh or a rebuilt view never loses what was typed.</summary>
public static class GitHubDrafts
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Texts = new();

    public static string Get(string key) => Texts.GetValueOrDefault(key, "");

    public static void Set(string key, string text)
    {
        if (text.Length == 0) Texts.TryRemove(key, out _); else Texts[key] = text;
    }
}

/// <summary>Comments posted from the app and not yet seen in a refresh: shown at once, dropped when the refresh returns them.</summary>
public static class GitHubPending
{
    public static IReadOnlyList<GitHubEvent> Remaining(IReadOnlyList<GitHubEvent> pending, IReadOnlyList<GitHubEvent> timeline) =>
        [.. pending.Where(item => item.Id.Length == 0 || !timeline.Any(seen => seen.Kind == "comment" && seen.Id == item.Id))];

    public static IReadOnlyList<GitHubCard> Remaining(IReadOnlyList<GitHubCard> pending, IReadOnlyList<GitHubCard> cards) =>
        [.. pending.Where(item => item.Id.Length == 0 || !cards.Any(seen => seen.Id == item.Id))];
}

public sealed record GitHubCheck(string Name, string State);

/// <summary>Pull request facts that an issue lacks.</summary>
public sealed record GitHubPullFacts(string BaseRef, string HeadRef, int Additions, int Deletions, int ChangedFiles, string Review, string Mergeability,
    string Checks, IReadOnlyList<GitHubCheck> CheckRuns);

public sealed record GitHubDetailParams(string WorkspaceId, string Kind, int Number, string? Revision = null);

public sealed record GitHubDetail(bool Unchanged, string Revision, string Repo, string Kind, int Number, string Title, string State, bool Draft, string Author,
    string CreatedAt, string Url, string Body, IReadOnlyList<GitHubLabel> Labels, IReadOnlyList<string> Assignees, string? Milestone,
    IReadOnlyList<GitHubEvent> Timeline, bool Truncated, GitHubPullFacts? Pull, string? Problem = null)
{
    /// <summary>Set when the header and body loaded but the timeline did not.</summary>
    public string? TimelineProblem { get; init; }
    /// <summary>The assignees with their avatars (the same people as Assignees).</summary>
    public IReadOnlyList<GitHubPerson> People { get; set; } = [];

    /// <summary>Not null when this answer is the saved copy.</summary>
    public GitHubCacheNote? Cached { get; init; }
}

public sealed record GitHubInfoParams(string WorkspaceId);

/// <summary>What the Settings tab shows: the gh account, the repository, and how much the host remembers (revisions only, never content).</summary>
public sealed record GitHubInfo(bool SignedIn, string Account, string Repo, string Url, bool DiscussionsEnabled, int PollSeconds, int CachedRevisions, string? Problem = null);

/// <summary>Asks for the size of the local copy of GitHub answers; Clear deletes it first.</summary>
public sealed record GitHubCacheQuery(bool Clear);

/// <summary>Bytes the local copy takes on disk, or the bytes freed when it was cleared.</summary>
public sealed record GitHubCacheSize(long Bytes);

/// <summary>Asks for one avatar image by the address GitHub gave (a public https address on githubusercontent.com).</summary>
public sealed record GitHubAvatarQuery(string Url);

/// <summary>The image bytes (a small PNG, JPEG, GIF or WebP), or null when the address is not allowed or the image could not be fetched.</summary>
public sealed record GitHubAvatarData(byte[]? Bytes);

/// <summary>The one form an avatar address is fetched, cached and looked up in: public https on githubusercontent.com, the 48 pixel size, no volatile query parts.</summary>
public static class GitHubAvatarUrl
{
    public const int Size = 48;

    public static Uri? Normalize(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host != "githubusercontent.com" && !host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)) return null;
        return new Uri("https://" + host + uri.AbsolutePath + "?s=" + Size);
    }
}

public static class GitHubContract
{
    public const string Id = "github";

    public static readonly PluginMethod<GitHubSignInQuery, GitHubSignIn> SignIn = new("signIn");
    public static readonly PluginMethod<GitHubListParams, GitHubList> List = new("list");
    public static readonly PluginMethod<GitHubDetailParams, GitHubDetail> Detail = new("detail");
    public static readonly PluginMethod<GitHubInfoParams, GitHubInfo> Info = new("info");
    public static readonly PluginMethod<GitHubViewParams, GitHubView> View = new("view");
    public static readonly PluginMethod<GitHubPostParams, GitHubPostResult> Post = new("post");
    public static readonly PluginMethod<GitHubLogParams, GitHubLog> Log = new("log");
    public static readonly PluginMethod<GitHubCacheQuery, GitHubCacheSize> Cache = new("cache");
    public static readonly PluginMethod<GitHubContributorsParams, GitHubContributors> ContributorStats = new("contributors");
    public static readonly PluginMethod<GitHubAvatarQuery, GitHubAvatarData> Avatar = new("avatar");
    public static readonly PluginContract Contract = PluginContract.Create<GitHubSettings>(Id, 1, [SignIn, List, Detail, Info, View, Post, Log, Cache, ContributorStats, Avatar], [], GitHubJson.Default);

    public const string Tool = "github";
    public const string Scheme = "github";
    public static readonly PluginManifest Manifest = new(Id, "GitHub", "asset:github.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.VersionControl,
        // Runs the gh command-line tool on the host's operating system, which a browser tab does not have.
        UnavailableOn = [HostKind.PureWeb],
        Description = "Pull requests and GitHub sign-in for github.com remotes through the gh command-line tool: lookup, open, update, ready, merge and reopen.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        Contributes = new() { SideTools = [new(Tool, "GitHub", "asset:github.svg", PluginToolSide.Right)] },
        Assets = "assets",
        Host = "Tereditor.Plugins.GitHub.Host.dll",
        Ui = "Tereditor.Plugins.GitHub.UI.dll"
    };
}

[JsonSerializable(typeof(GitHubSettings))]
[JsonSerializable(typeof(GitHubSignInQuery))]
[JsonSerializable(typeof(GitHubSignIn))]
[JsonSerializable(typeof(GitHubListParams))]
[JsonSerializable(typeof(GitHubList))]
[JsonSerializable(typeof(GitHubPerson))]
[JsonSerializable(typeof(GitHubDetailParams))]
[JsonSerializable(typeof(GitHubDetail))]
[JsonSerializable(typeof(GitHubInfoParams))]
[JsonSerializable(typeof(GitHubInfo))]
[JsonSerializable(typeof(GitHubViewParams))]
[JsonSerializable(typeof(GitHubView))]
[JsonSerializable(typeof(GitHubPostParams))]
[JsonSerializable(typeof(GitHubPostResult))]
[JsonSerializable(typeof(GitHubLogParams))]
[JsonSerializable(typeof(GitHubLog))]
[JsonSerializable(typeof(GitHubCacheQuery))]
[JsonSerializable(typeof(GitHubCacheSize))]
[JsonSerializable(typeof(GitHubContributorsParams))]
[JsonSerializable(typeof(GitHubContributors))]
[JsonSerializable(typeof(GitHubAvatarQuery))]
[JsonSerializable(typeof(GitHubAvatarData))]
public sealed partial class GitHubJson : JsonSerializerContext;

/// <summary>Where a GitHub link points inside this repository; everything else is not ours to open.</summary>
public static partial class GitHubLinks
{
    /// <summary>The plugin document id and tab title for a link to this repository's issue, pull request, discussion, run or releases; null for any other link.</summary>
    public static (string Id, string Title)? Parse(string url, string repo)
    {
        if (repo.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 3 || !string.Equals(parts[0] + "/" + parts[1], repo, StringComparison.OrdinalIgnoreCase)) return null;
        var number = parts.Length > 3 && int.TryParse(parts[3], out var n) && n > 0 ? n : 0;
        return (parts[2], number, parts.Length > 4 ? parts[4] : "") switch
        {
            ("issues", > 0, _) => ($"issue:{number}", $"Issue #{number}"),
            ("pull", > 0, _) => ($"pull:{number}", $"PR #{number}"),
            ("discussions", > 0, _) => ($"discussion:{number}", $"Discussion #{number}"),
            ("actions", _, _) when parts.Length > 4 && parts[3] == "runs" && long.TryParse(parts[4], out var run) => ($"run:{run}", $"Run {run}"),
            ("releases", _, _) => ("releases", "Releases"),
            _ => null
        };
    }

    /// <summary>Turns bare #N references into links to this repository's issues, outside code.</summary>
    public static string Rewrite(string text, string repo)
    {
        if (repo.Length == 0) return text;
        var fenced = false;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (fenced) continue;
            var spans = lines[i].Split('`');
            for (var j = 0; j < spans.Length; j += 2) spans[j] = Reference().Replace(spans[j], m => $"[#{m.Groups[1].Value}](https://github.com/{repo}/issues/{m.Groups[1].Value})");
            lines[i] = string.Join('`', spans);
        }
        return string.Join('\n', lines);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w&/#\[(])#(\d+)\b(?!\])")]
    private static partial System.Text.RegularExpressions.Regex Reference();
}