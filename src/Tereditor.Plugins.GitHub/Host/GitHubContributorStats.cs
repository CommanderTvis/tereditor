using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Tereditor.Plugins.GitHub.Host;

/// <summary>Contributor statistics from <c>repos/{repo}/stats/contributors</c>: one answer per repository for the session.</summary>
public sealed partial class GitHubBrowser
{
    private readonly ConcurrentDictionary<string, GitHubContributors> contributorCache = new();

    public async Task<GitHubContributors> ContributorsAsync(string directory, GitHubContributorsParams p, CancellationToken ct)
    {
        try
        {
            if (Offline) return ContributorProblem("GitHub is switched off for this session.");
            var repo = await RepoAsync(directory, ct);
            if (!contributorCache.TryGetValue(repo.Slug, out var cached))
            {
                var branch = "";
                using (var info = await RestAsync(directory, "repos/" + repo.Slug, ct)) branch = Text(info.RootElement, "default_branch");
                var (status, body) = await StatsAsync(directory, repo.Slug, ct);
                if (status == 202) return new(false, "", branch, true, [], []);
                cached = status == 204 || body.Trim().Length == 0 ? Revised(new(false, "", branch, false, [], [])) : Revised(ParseStats(body, branch));
                contributorCache[repo.Slug] = cached;
            }
            return p.Revision == cached.Revision ? cached with { Unchanged = true, Weeks = [], Authors = [] } : cached;
        }
        catch (Exception error) when (IsExpected(error)) { return ContributorProblem(Describe(error)); }
    }

    private static GitHubContributors ContributorProblem(string message) => new(false, "", "", false, [], [], message);

    private GitHubContributors Revised(GitHubContributors value) =>
        value with { Revision = Revise("contributors|" + value.Branch, JsonSerializer.Serialize(value, GitHubJson.Default.GitHubContributors)) };

    /// <summary>The HTTP status and body; gh answers 202 and 204 with an empty body, so the headers are asked for.</summary>
    private async Task<(int Status, string Body)> StatsAsync(string directory, string slug, CancellationToken ct)
    {
        var result = await run("gh", directory, ["api", "--include", $"repos/{slug}/stats/contributors"], Query, GhEnvironment, ct);
        if (result.ExitCode != 0) throw new IOException(Trim(result.Error, "gh could not read GitHub."));
        var text = result.Output;
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var crlf and >= 0 ? (crlf, 4) : (text.IndexOf("\n\n", StringComparison.Ordinal), 2);
        if (!text.StartsWith("HTTP/", StringComparison.Ordinal) || split.Item1 < 0) return (200, text);
        var line = text[..text.IndexOf('\n')].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var status = line.Length > 1 && int.TryParse(line[1], CultureInfo.InvariantCulture, out var code) ? code : 200;
        return (status, text[(split.Item1 + split.Item2)..]);
    }

    internal static GitHubContributors ParseStats(string body, string branch)
    {
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return new(false, "", branch, false, [], []);
        var weeks = new SortedSet<long>();
        foreach (var author in json.RootElement.EnumerateArray())
            if (author.TryGetProperty("weeks", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var week in list.EnumerateArray()) weeks.Add(week.GetProperty("w").GetInt64());
        var index = weeks.Select((w, i) => (w, i)).ToDictionary(pair => pair.w, pair => pair.i);
        var authors = new List<GitHubContributor>();
        foreach (var author in json.RootElement.EnumerateArray())
        {
            var who = author.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            if (who.ValueKind != JsonValueKind.Object) continue;
            int[] commits = new int[weeks.Count], additions = new int[weeks.Count], deletions = new int[weeks.Count];
            if (author.TryGetProperty("weeks", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var week in list.EnumerateArray())
                {
                    var at = index[week.GetProperty("w").GetInt64()];
                    commits[at] = week.GetProperty("c").GetInt32(); additions[at] = week.GetProperty("a").GetInt32(); deletions[at] = week.GetProperty("d").GetInt32();
                }
            authors.Add(new(Text(who, "login"), Text(who, "avatar_url"), Text(who, "html_url"), commits, additions, deletions));
        }
        return new(false, "", branch, false, [.. weeks], authors);
    }
}