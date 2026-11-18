using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tereditor.Plugins.GitHub.Host;

/// <summary>Actions, Discussions, Releases, Contributors, Deployments and Packages: generic rows and cards, read through gh.</summary>
public sealed partial class GitHubBrowser
{
    private static readonly string[] Events = ["push", "pull_request", "schedule", "workflow_dispatch", "release", "workflow_run"];
    private static readonly string[] Statuses = ["queued", "in_progress", "completed", "success", "failure", "cancelled", "skipped"];
    private static readonly string[] PackageTypes = ["container", "npm", "maven", "nuget", "rubygems"];

    public async Task<GitHubView> ViewAsync(string directory, GitHubViewParams p, CancellationToken ct)
    {
        try
        {
            if (Offline) return Problem("GitHub is switched off for this session.");
            var repo = await RepoAsync(directory, ct);
            var filters = p.Filters.Where(f => f.Value.Length > 0).GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.Last().Value);
            var limit = Math.Clamp(p.Limit, 1, 100);
            var view = (p.Kind, p.Id.Length > 0) switch
            {
                ("runs", false) => await RunsAsync(directory, repo, filters, limit, ct),
                ("runs", true) => await RunAsync(directory, repo, p.Id, ct),
                ("discussions", false) => await DiscussionsAsync(directory, repo, filters, limit, ct),
                ("discussions", true) => await DiscussionAsync(directory, repo, p.Id, ct),
                ("releases", _) => await ReleasesAsync(directory, repo, limit, ct),
                ("contributors", _) => await ContributorsAsync(directory, repo, limit, ct),
                ("deployments", _) => await DeploymentsAsync(directory, repo, limit, ct),
                ("packages", _) => await PackagesAsync(directory, repo, filters, ct),
                _ => Problem("Unknown view: " + p.Kind)
            };
            if (view.Problem is not null) return view;
            var key = $"view|{repo.Slug}|{p.Kind}|{p.Id}|{string.Join(',', filters.OrderBy(f => f.Key).Select(f => f.Key + "=" + f.Value))}|{limit}";
            var revision = Revise(key, JsonSerializer.Serialize(view, GitHubJson.Default.GitHubView));
            return p.Revision == revision ? view with { Unchanged = true, Revision = revision, Rows = [], Cards = [] } : view with { Revision = revision };
        }
        catch (Exception error) when (IsExpected(error)) { return Problem(Describe(error)); }
    }

    private static GitHubView Problem(string message) => new(false, "", "", "", "none", [], [], [], false, "", message);

    private async Task<JsonDocument> RestAsync(string directory, string path, CancellationToken ct)
    {
        var result = await run("gh", directory, ["api", path], Query, GhEnvironment, ct);
        if (result.ExitCode != 0) throw new IOException(Trim(result.Error, "gh could not read GitHub."));
        return JsonDocument.Parse(result.Output);
    }

    private static string Enc(string value) => Uri.EscapeDataString(value);

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values, string current) =>
        [.. values.Append(current).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string Duration(string from, string to)
    {
        if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, out var start) || !DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, out var end) || end < start) return "";
        var span = end - start;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }

    private static string RunState(JsonElement run) => Text(run, "status") switch
    {
        "completed" => Text(run, "conclusion") switch { "success" => "success", "cancelled" => "cancelled", "skipped" or "neutral" => "skipped", _ => "failure" },
        "queued" or "waiting" or "requested" or "pending" => "queued",
        _ => "running"
    };

    private static string Describe(string eventName, string actor, JsonElement run) => eventName switch
    {
        "schedule" => "Scheduled",
        "pull_request" or "pull_request_target" => "Pull request " + PullNumber(run) + "by " + actor,
        "workflow_dispatch" => "Manually run by " + actor,
        "push" => "Commit " + Short(Text(run, "head_sha")) + "pushed by " + actor,
        _ => eventName.Replace('_', ' ') + " by " + actor
    };

    private static string PullNumber(JsonElement run) =>
        run.TryGetProperty("pull_requests", out var pulls) && pulls.ValueKind == JsonValueKind.Array && pulls.GetArrayLength() > 0 ? "#" + pulls[0].GetProperty("number").GetInt32() + " " : "";

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] + " " : "";

    private async Task<GitHubView> RunsAsync(string directory, RepoFacts repo, Dictionary<string, string> filters, int limit, CancellationToken ct)
    {
        var path = new StringBuilder($"repos/{repo.Slug}/actions/runs?per_page={limit}");
        foreach (var (key, value) in filters)
            if (key is "event" or "status" or "branch" or "actor") path.Append('&').Append(key).Append('=').Append(Enc(value));
        using var json = await RestAsync(directory, path.ToString(), ct);
        var root = json.RootElement;
        var runs = root.GetProperty("workflow_runs").EnumerateArray().ToList();
        var rows = runs.Select(run =>
        {
            var actor = run.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.Object ? Text(a, "login") : "";
            var started = Text(run, "run_started_at") is { Length: > 0 } s ? s : Text(run, "created_at");
            var state = RunState(run);
            return new GitHubRow(run.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture), Text(run, "display_title"), state,
                $"{Text(run, "name")} #{run.GetProperty("run_number").GetInt32()}: {Describe(Text(run, "event"), actor, run)}",
                Text(run, "head_branch") is { Length: > 0 } branch ? [new(branch, "")] : [], state is "success" or "failure" or "cancelled" or "skipped" ? Duration(started, Text(run, "updated_at")) : "",
                Text(run, "html_url"), Text(run, "created_at"));
        }).ToList();
        var total = root.GetProperty("total_count").GetInt32();
        IReadOnlyList<GitHubFilter> chips =
        [
            new("event", "Event", Events, filters.GetValueOrDefault("event", "")),
            new("status", "Status", Statuses, filters.GetValueOrDefault("status", "")),
            new("branch", "Branch", Distinct(runs.Select(r => Text(r, "head_branch")), filters.GetValueOrDefault("branch", "")), filters.GetValueOrDefault("branch", "")),
            new("actor", "Actor", Distinct(runs.Select(r => r.TryGetProperty("actor", out var a) ? Text(a, "login") : ""), filters.GetValueOrDefault("actor", "")), filters.GetValueOrDefault("actor", ""))
        ];
        return new(false, "", "All workflows", "Showing runs from all workflows", "none", rows, [], chips, total > rows.Count, total.ToString("N0", CultureInfo.InvariantCulture) + " workflow runs");
    }

    private async Task<GitHubView> RunAsync(string directory, RepoFacts repo, string id, CancellationToken ct)
    {
        if (!long.TryParse(id, CultureInfo.InvariantCulture, out var number)) return Problem("Not a run id: " + id);
        using var run = await RestAsync(directory, $"repos/{repo.Slug}/actions/runs/{number}", ct);
        using var jobs = await RestAsync(directory, $"repos/{repo.Slug}/actions/runs/{number}/jobs?per_page=100", ct);
        var r = run.RootElement;
        var actor = r.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.Object ? Text(a, "login") : "";
        var rows = new List<GitHubRow>();
        foreach (var job in jobs.RootElement.GetProperty("jobs").EnumerateArray())
        {
            rows.Add(new(job.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture), Text(job, "name"), RunState(job), "", [], Duration(Text(job, "started_at"), Text(job, "completed_at")), Text(job, "html_url"), Text(job, "started_at")));
            if (job.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                foreach (var step in steps.EnumerateArray())
                    rows.Add(new(Text(job, "name") + "/" + step.GetProperty("number").GetInt32(), Text(step, "name"), RunState(step), "", [], Duration(Text(step, "started_at"), Text(step, "completed_at")), "", "", 1));
        }
        var state = RunState(r);
        var branch = Text(r, "head_branch");
        return new(false, "", Text(r, "display_title") + " #" + r.GetProperty("run_number").GetInt32(), $"{Text(r, "name")} · {Describe(Text(r, "event"), actor, r).Trim()}" + (branch.Length > 0 ? " on " + branch : "") + " · triggered " + Text(r, "created_at"),
            state, rows, [], [], false, Text(r, "html_url"));
    }

    private async Task<GitHubView> DiscussionsAsync(string directory, RepoFacts repo, Dictionary<string, string> filters, int limit, CancellationToken ct)
    {
        if (!repo.Discussions) return Problem("Discussions are not turned on for this repository.");
        using var categories = await GraphQlAsync(directory, "query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { discussionCategories(first: 25) { nodes { id name emoji } } } }", ct, ("owner", repo.Owner), ("name", repo.Name));
        var found = categories.RootElement.GetProperty("data").GetProperty("repository").GetProperty("discussionCategories").GetProperty("nodes").EnumerateArray().Select(n => (Id: Text(n, "id"), Name: Text(n, "name"))).ToList();
        var wanted = filters.GetValueOrDefault("category", "");
        var categoryId = found.FirstOrDefault(c => c.Name == wanted).Id;
        var variables = new List<(string, object)> { ("owner", repo.Owner), ("name", repo.Name), ("first", limit) };
        if (!string.IsNullOrEmpty(categoryId)) variables.Add(("category", categoryId));
        using var json = await GraphQlAsync(directory, "query($owner: String!, $name: String!, $first: Int!, $category: ID) { repository(owner: $owner, name: $name) { discussions(first: $first, categoryId: $category, orderBy: {field: CREATED_AT, direction: DESC}) { totalCount pageInfo { hasNextPage } nodes { number title url createdAt author { login } category { name emoji } comments { totalCount } answer { id } closed upvoteCount } } } }", ct, [.. variables]);
        var list = json.RootElement.GetProperty("data").GetProperty("repository").GetProperty("discussions");
        var rows = list.GetProperty("nodes").EnumerateArray().Select(n => new GitHubRow(n.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture), Text(n, "title"),
            n.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.Object ? "answered" : Bool(n, "closed") ? "closed" : "open",
            $"#{n.GetProperty("number").GetInt32()} opened by {Author(n)} in {Text(n.GetProperty("category"), "emoji")} {Text(n.GetProperty("category"), "name")}".Replace("  ", " "),
            [], n.GetProperty("comments").GetProperty("totalCount").GetInt32() + " comments · " + n.GetProperty("upvoteCount").GetInt32() + " upvotes", Text(n, "url"), Text(n, "createdAt"))).ToList();
        GitHubFilter[] chips = [new("category", "Category", [.. found.Select(c => c.Name)], wanted)];
        return new(false, "", "Discussions", "", "none", rows, [], chips, list.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), list.GetProperty("totalCount").GetInt32() + " discussions");
    }

    private async Task<GitHubView> DiscussionAsync(string directory, RepoFacts repo, string id, CancellationToken ct)
    {
        if (!int.TryParse(id, CultureInfo.InvariantCulture, out var number)) return Problem("Not a discussion number: " + id);
        using var json = await GraphQlAsync(directory, "query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { discussion(number: $number) { number title body url createdAt author { login } category { name emoji } closed answer { id } "
            + "comments(first: 50) { nodes { id databaseId author { login } createdAt body replies(first: 30) { nodes { author { login } createdAt body } } } } } } }", ct, ("owner", repo.Owner), ("name", repo.Name), ("number", number));
        var d = json.RootElement.GetProperty("data").GetProperty("repository").GetProperty("discussion");
        if (d.ValueKind != JsonValueKind.Object) return Problem("That discussion does not exist.");
        var answerId = d.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.Object ? Text(answer, "id") : "";
        var cards = new List<GitHubCard> { new("", Author(d), Text(d, "createdAt"), "", Text(d, "body")) };
        foreach (var comment in d.GetProperty("comments").GetProperty("nodes").EnumerateArray())
        {
            cards.Add(new("", Author(comment), Text(comment, "createdAt"), "", Text(comment, "body"), Text(comment, "id") == answerId ? "Answer" : "") { Id = comment.TryGetProperty("databaseId", out var dbid) && dbid.ValueKind == JsonValueKind.Number ? dbid.GetInt64().ToString(CultureInfo.InvariantCulture) : "" });
            foreach (var reply in comment.GetProperty("replies").GetProperty("nodes").EnumerateArray()) cards.Add(new("", Author(reply), Text(reply, "createdAt"), "replied", Text(reply, "body")));
        }
        return new(false, "", Text(d, "title") + " #" + number, $"{Author(d)} started this discussion in {Text(d.GetProperty("category"), "emoji")} {Text(d.GetProperty("category"), "name")}", answerId.Length > 0 ? "answered" : Bool(d, "closed") ? "closed" : "open", [], cards, [], false, Text(d, "url"));
    }

    private async Task<GitHubView> ReleasesAsync(string directory, RepoFacts repo, int limit, CancellationToken ct)
    {
        using var json = await RestAsync(directory, $"repos/{repo.Slug}/releases?per_page={limit}", ct);
        var items = json.RootElement.EnumerateArray().ToList();
        var cards = items.Select((r, index) =>
        {
            var body = new StringBuilder(Text(r, "body"));
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array && assets.GetArrayLength() > 0)
            {
                body.Append("\n\n**Assets** ").Append(assets.GetArrayLength()).Append('\n');
                foreach (var asset in assets.EnumerateArray()) body.Append("\n- ").Append(Text(asset, "name")).Append(" (").Append(Size(asset.GetProperty("size").GetInt64())).Append(')');
            }
            var tag = Text(r, "tag_name");
            var name = Text(r, "name") is { Length: > 0 } n ? n : tag;
            var badge = Bool(r, "draft") ? "Draft" : Bool(r, "prerelease") ? "Pre-release" : r.Equals(items.FirstOrDefault(i => !Bool(i, "draft") && !Bool(i, "prerelease"))) ? "Latest" : "";
            return new GitHubCard(name, r.TryGetProperty("author", out var a) ? Text(a, "login") : "", Text(r, "published_at") is { Length: > 0 } p ? p : Text(r, "created_at"), "released this · " + tag, body.ToString(), badge, Text(r, "html_url"));
        }).ToList();
        return new(false, "", "Releases", "", "none", [], cards, [], items.Count >= limit, items.Count + " releases");
    }

    private static string Size(long bytes) => bytes >= 1 << 20 ? (bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";

    private async Task<GitHubView> ContributorsAsync(string directory, RepoFacts repo, int limit, CancellationToken ct)
    {
        using var json = await RestAsync(directory, $"repos/{repo.Slug}/contributors?per_page={limit}", ct);
        var rows = json.RootElement.ValueKind != JsonValueKind.Array ? [] : json.RootElement.EnumerateArray()
            .Select(c => new GitHubRow(Text(c, "login"), Text(c, "login"), "none", Text(c, "type") == "Bot" ? "Bot" : "", [], c.GetProperty("contributions").GetInt32() + " commits", Text(c, "html_url"))).ToList();
        return new(false, "", "Contributors", "Contributions to the default branch, most first", "none", rows, [], [], rows.Count >= limit, rows.Count + " contributors");
    }

    private async Task<GitHubView> DeploymentsAsync(string directory, RepoFacts repo, int limit, CancellationToken ct)
    {
        using var json = await GraphQlAsync(directory, "query($owner: String!, $name: String!, $first: Int!) { repository(owner: $owner, name: $name) { deployments(first: $first, orderBy: {field: CREATED_AT, direction: DESC}) { totalCount pageInfo { hasNextPage } nodes { id environment ref { name } commit { abbreviatedOid } creator { login } createdAt latestStatus { state } } } } }", ct, ("owner", repo.Owner), ("name", repo.Name), ("first", limit));
        var list = json.RootElement.GetProperty("data").GetProperty("repository").GetProperty("deployments");
        var rows = list.GetProperty("nodes").EnumerateArray().Select(n =>
        {
            var status = n.TryGetProperty("latestStatus", out var s) && s.ValueKind == JsonValueKind.Object ? Text(s, "state") : "";
            var state = status switch { "SUCCESS" or "ACTIVE" => "success", "FAILURE" or "ERROR" => "failure", "INACTIVE" => "skipped", "IN_PROGRESS" => "running", "QUEUED" or "PENDING" or "WAITING" => "queued", _ => "none" };
            var branch = n.TryGetProperty("ref", out var rf) && rf.ValueKind == JsonValueKind.Object ? Text(rf, "name") : "";
            var sha = n.TryGetProperty("commit", out var cm) && cm.ValueKind == JsonValueKind.Object ? Text(cm, "abbreviatedOid") : "";
            return new GitHubRow(Text(n, "id"), Text(n, "environment"), state, $"{(n.TryGetProperty("creator", out var cr) ? Text(cr, "login") : "")} deployed {sha}", branch.Length > 0 ? [new(branch, "")] : [], status.Length > 0 ? status.ToLowerInvariant().Replace('_', ' ') : "no status", "", Text(n, "createdAt"));
        }).ToList();
        return new(false, "", "Deployments", "", "none", rows, [], [], list.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean(), list.GetProperty("totalCount").GetInt32() + " deployments");
    }

    private async Task<GitHubView> PackagesAsync(string directory, RepoFacts repo, Dictionary<string, string> filters, CancellationToken ct)
    {
        var type = filters.GetValueOrDefault("type", "container");
        JsonDocument? json = null;
        foreach (var owner in new[] { "orgs", "users" })
        {
            try { json = await RestAsync(directory, $"{owner}/{repo.Owner}/packages?package_type={Enc(type)}&per_page=100", ct); break; }
            catch (IOException error) when (error.Message.Contains("read:packages", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("scope", StringComparison.OrdinalIgnoreCase))
            {
                return Problem("gh needs the read:packages scope to list packages. Run: gh auth refresh -s read:packages");
            }
            catch (IOException) when (owner == "orgs") { }
        }
        if (json is null) return Problem("GitHub did not list packages for " + repo.Owner + ".");
        using (json)
        {
            var rows = json.RootElement.EnumerateArray()
                .Where(p => p.TryGetProperty("repository", out var r) && r.ValueKind == JsonValueKind.Object && string.Equals(Text(r, "full_name"), repo.Slug, StringComparison.OrdinalIgnoreCase))
                .Select(p => new GitHubRow(Text(p, "name"), Text(p, "name"), "none", Text(p, "visibility"), [new(type, "")], p.TryGetProperty("version_count", out var v) ? v.GetInt32() + " versions" : "", Text(p, "html_url"), Text(p, "updated_at"))).ToList();
            return new(false, "", "Packages", "Packages of this repository, by type", "none", rows, [], [new("type", "Type", PackageTypes, type)], false, rows.Count + " packages");
        }
    }
}