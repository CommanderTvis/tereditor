using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.GitHub;

public sealed record GitHubSettings
{
    /// <summary>Seconds between refreshes of an open GitHub tab; 15 to 600.</summary>
    public int PollSeconds { get; set; } = 60;
}

public sealed record GitHubSignInQuery;

/// <summary>Whether <c>gh</c> is signed in on the host, with the line to show.</summary>
public sealed record GitHubSignIn(bool Connected, string Detail);

/// <summary>One label with GitHub's own hex colour (no #).</summary>
public sealed record GitHubLabel(string Name, string Color);

/// <summary>One row of an issue or pull request list. State is open, closed or merged; Checks is passing, failing, pending or none.</summary>
public sealed record GitHubItem(int Number, string Title, string State, bool Draft, string Author, IReadOnlyList<GitHubLabel> Labels, int Comments,
    string CreatedAt, string UpdatedAt, string? ClosedAt, string Url, string Checks, string Review);

/// <summary>Kind is issues or pulls. Query is GitHub's search syntax without the repository and kind qualifiers. Revision is what the caller holds; the host answers Unchanged when it still matches.</summary>
public sealed record GitHubListParams(string WorkspaceId, string Kind, string Query, int Limit = 25, string? Revision = null);

public sealed record GitHubList(bool Unchanged, string Revision, string Repo, IReadOnlyList<GitHubItem> Items, int Open, int Closed, bool HasMore, string? Problem = null)
{
    /// <summary>The repository's labels, for the Label filter.</summary>
    public IReadOnlyList<GitHubLabel> RepoLabels { get; init; } = [];
}

/// <summary>One filter chip a view offers; Value is what is chosen now ("" for none).</summary>
public sealed record GitHubFilter(string Key, string Label, IReadOnlyList<string> Options, string Value = "");

public sealed record GitHubFilterValue(string Key, string Value);

/// <summary>One row of a generic view. State: open, closed, merged, success, failure, cancelled, skipped, running, queued, answered, none.</summary>
public sealed record GitHubRow(string Id, string Title, string State, string Meta, IReadOnlyList<GitHubLabel> Chips, string Right, string Url, string At = "", int Indent = 0);

/// <summary>A Markdown card: a release, a discussion post or a reply.</summary>
public sealed record GitHubCard(string Title, string Author, string At, string Note, string Body, string Badge = "", string Url = "");

/// <summary>Kind: runs, discussions, releases, contributors, deployments or packages. Id is empty for the list, else the run id or discussion number.</summary>
public sealed record GitHubViewParams(string WorkspaceId, string Kind, string Id, IReadOnlyList<GitHubFilterValue> Filters, int Limit = 30, string? Revision = null);

public sealed record GitHubView(bool Unchanged, string Revision, string Title, string Subtitle, string State, IReadOnlyList<GitHubRow> Rows, IReadOnlyList<GitHubCard> Cards,
    IReadOnlyList<GitHubFilter> Filters, bool HasMore, string Summary = "", string? Problem = null);

public sealed record GitHubReviewComment(string Path, string Body);

/// <summary>One timeline entry. Kind: comment, review, commit, labeled, unlabeled, closed, reopened, merged, assigned, unassigned, renamed, milestoned, referenced, review-requested, ready, draft, force-pushed, branch-deleted.</summary>
public sealed record GitHubEvent(string Kind, string Author, string At, string Body, string Detail, string Color = "", IReadOnlyList<GitHubReviewComment>? Comments = null);

public sealed record GitHubCheck(string Name, string State);

/// <summary>Pull request facts that an issue lacks.</summary>
public sealed record GitHubPullFacts(string BaseRef, string HeadRef, int Additions, int Deletions, int ChangedFiles, string Review, string Mergeability,
    string Checks, IReadOnlyList<GitHubCheck> CheckRuns);

public sealed record GitHubDetailParams(string WorkspaceId, string Kind, int Number, string? Revision = null);

public sealed record GitHubDetail(bool Unchanged, string Revision, string Repo, string Kind, int Number, string Title, string State, bool Draft, string Author,
    string CreatedAt, string Url, string Body, IReadOnlyList<GitHubLabel> Labels, IReadOnlyList<string> Assignees, string? Milestone,
    IReadOnlyList<GitHubEvent> Timeline, bool Truncated, GitHubPullFacts? Pull, string? Problem = null);

public sealed record GitHubInfoParams(string WorkspaceId);

/// <summary>What the Settings tab shows: the gh account, the repository, and how much the host remembers (revisions only, never content).</summary>
public sealed record GitHubInfo(bool SignedIn, string Account, string Repo, string Url, bool DiscussionsEnabled, int PollSeconds, int CachedRevisions, string? Problem = null);

public static class GitHubContract
{
    public const string Id = "github";

    public static readonly PluginMethod<GitHubSignInQuery, GitHubSignIn> SignIn = new("signIn");
    public static readonly PluginMethod<GitHubListParams, GitHubList> List = new("list");
    public static readonly PluginMethod<GitHubDetailParams, GitHubDetail> Detail = new("detail");
    public static readonly PluginMethod<GitHubInfoParams, GitHubInfo> Info = new("info");
    public static readonly PluginMethod<GitHubViewParams, GitHubView> View = new("view");
    public static readonly PluginContract Contract = PluginContract.Create<GitHubSettings>(Id, 1, [SignIn, List, Detail, Info, View], [], GitHubJson.Default);

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
[JsonSerializable(typeof(GitHubDetailParams))]
[JsonSerializable(typeof(GitHubDetail))]
[JsonSerializable(typeof(GitHubInfoParams))]
[JsonSerializable(typeof(GitHubInfo))]
[JsonSerializable(typeof(GitHubViewParams))]
[JsonSerializable(typeof(GitHubView))]
public sealed partial class GitHubJson : JsonSerializerContext;