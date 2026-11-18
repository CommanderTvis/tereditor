using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tereditor.Plugins.GitHub.Host;

/// <summary>Posting comments and closing or reopening, and reading one job's log. Only a click in the UI reaches PostAsync.</summary>
public sealed partial class GitHubBrowser
{
    private const int LogGroupLines = 500;
    private const int LogTotalLines = 4000;
    private readonly ConcurrentDictionary<string, GitHubLog> logs = new();

    public async Task<GitHubPostResult> PostAsync(string directory, GitHubPostParams p, CancellationToken ct)
    {
        var discussion = p.Kind == "discussions";
        GitHubEvent? posted = null;
        try
        {
            if (Offline) return new(false, "GitHub is switched off for this session.");
            var body = p.Body.Trim();
            if (p.Action == "comment" && body.Length == 0) return new(false, "Write a comment first.");
            var repo = await RepoAsync(directory, ct);
            if (discussion)
            {
                using var found = await GraphQlAsync(directory, "query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { discussion(number: $number) { id } } }", ct, ("owner", repo.Owner), ("name", repo.Name), ("number", p.Number));
                var id = Text(found.RootElement.GetProperty("data").GetProperty("repository").GetProperty("discussion"), "id");
                using var made = await GraphQlAsync(directory, "mutation($id: ID!, $body: String!) { addDiscussionComment(input: {discussionId: $id, body: $body}) { comment { databaseId createdAt body author { login } } } }", ct, ("id", id), ("body", body));
                var comment = made.RootElement.GetProperty("data").GetProperty("addDiscussionComment").GetProperty("comment");
                return new(true, null, new("comment", Author(comment), Text(comment, "createdAt"), Text(comment, "body"), "") { Id = comment.GetProperty("databaseId").GetInt64().ToString(CultureInfo.InvariantCulture) });
            }
            var number = p.Number.ToString(CultureInfo.InvariantCulture);
            if (body.Length > 0)
            {
                var result = await run("gh", directory, ["api", $"repos/{repo.Slug}/issues/{number}/comments", "-f", "body=" + body], Query, GhEnvironment, ct);
                if (result.ExitCode != 0) return new(false, WriteProblem(result.Error, "repo"));
                using var json = JsonDocument.Parse(result.Output);
                var root = json.RootElement;
                posted = new("comment", root.TryGetProperty("user", out var user) ? Text(user, "login") : "", Text(root, "created_at"), Text(root, "body"), "") { Id = root.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture) };
            }
            if (p.Action is "close" or "reopen")
            {
                var state = p.Action == "close" ? "closed" : "open";
                var result = await run("gh", directory, ["api", "-X", "PATCH", $"repos/{repo.Slug}/issues/{number}", "-f", "state=" + state], Query, GhEnvironment, ct);
                if (result.ExitCode != 0) return new(false, (posted is null ? "" : "The comment was posted, but the state did not change. ") + WriteProblem(result.Error, "repo"), posted);
                return new(true, null, posted, state);
            }
            return new(true, null, posted);
        }
        catch (Exception error) when (IsExpected(error)) { return new(false, WriteProblem(error is IOException ? error.Message : Describe(error), discussion ? "write:discussion" : "repo"), posted); }
    }

    private static string WriteProblem(string error, string scope)
    {
        var text = error.Trim().Length == 0 ? "gh could not post." : error.Trim().Split('\n')[0];
        return text.Contains("scope", StringComparison.OrdinalIgnoreCase) || text.Contains("403", StringComparison.Ordinal) || text.Contains("not accessible", StringComparison.OrdinalIgnoreCase)
            ? text + " gh may lack a write scope. Run: gh auth refresh -s " + scope
            : text;
    }

    public async Task<GitHubLog> LogAsync(string directory, GitHubLogParams p, CancellationToken ct)
    {
        try
        {
            if (Offline) return new([], 0, "", "GitHub is switched off for this session.");
            if (!long.TryParse(p.JobId, CultureInfo.InvariantCulture, out var id)) return new([], 0, "", "Not a job id: " + p.JobId);
            if (logs.TryGetValue(p.JobId, out var cached)) return cached;
            var repo = await RepoAsync(directory, ct);
            using var job = await RestAsync(directory, $"repos/{repo.Slug}/actions/jobs/{id}", ct);
            var url = Text(job.RootElement, "html_url");
            if (Text(job.RootElement, "status") != "completed") return new([], 0, url, "This job is still running. Its log is available when it finishes. This tab keeps refreshing.", true);
            var result = await run("gh", directory, ["api", $"repos/{repo.Slug}/actions/jobs/{id}/logs"], TimeSpan.FromSeconds(60), GhEnvironment, ct);
            if (result.ExitCode != 0) return new([], 0, url, Trim(result.Error, "gh could not read the log. It may have expired."));
            var log = ParseLog(result.Output, url);
            if (logs.Count > 40) logs.Clear();
            logs[p.JobId] = log;
            return log;
        }
        catch (Exception error) when (IsExpected(error)) { return new([], 0, "", Describe(error)); }
    }

    [GeneratedRegex("\\x1B\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"^(\d{4}-\d\d-\d\dT)(\d\d:\d\d:\d\d)[.\d]*Z ?")]
    private static partial Regex Stamp();

    private sealed class LogGroupBuilder(string name)
    {
        public string Name { get; } = name;
        public bool Error { get; set; }
        public List<GitHubLogLine> Lines { get; } = [];
    }

    /// <summary>Splits a job log into its ##[group] sections, drops colour codes, and keeps it bounded.</summary>
    public static GitHubLog ParseLog(string text, string url)
    {
        var groups = new List<LogGroupBuilder>();
        var current = new LogGroupBuilder("");
        groups.Add(current);
        foreach (var raw in text.Replace(((char)0xFEFF).ToString(), "").Split('\n'))
        {
            var line = Ansi().Replace(raw.TrimEnd('\r'), "");
            var time = "";
            if (Stamp().Match(line) is { Success: true } stamp) { time = stamp.Groups[2].Value; line = line[stamp.Length..]; }
            if (line.StartsWith("##[group]", StringComparison.Ordinal)) { current = new(line["##[group]".Length..]); groups.Add(current); continue; }
            if (line.StartsWith("##[endgroup]", StringComparison.Ordinal)) { current = new(""); groups.Add(current); continue; }
            var error = line.StartsWith("##[error]", StringComparison.Ordinal);
            if (error) { line = line["##[error]".Length..]; current.Error = true; }
            if (line.Length == 0 && time.Length == 0) continue;
            current.Lines.Add(new(time, line));
        }
        var cut = 0;
        var kept = new List<(string Name, bool Error, List<GitHubLogLine> Lines)>();
        foreach (var group in groups.Where(g => g.Lines.Count > 0 || g.Name.Length > 0))
        {
            var lines = group.Lines;
            if (lines.Count > LogGroupLines) { cut += lines.Count - LogGroupLines; lines = lines.GetRange(lines.Count - LogGroupLines, LogGroupLines); }
            kept.Add((group.Name, group.Error, lines));
        }
        var total = kept.Sum(g => g.Lines.Count);
        for (var i = 0; total > LogTotalLines && i < kept.Count; i++)
        {
            var drop = Math.Min(kept[i].Lines.Count, total - LogTotalLines);
            kept[i] = (kept[i].Name, kept[i].Error, kept[i].Lines.GetRange(drop, kept[i].Lines.Count - drop));
            total -= drop; cut += drop;
        }
        return new([.. kept.Select(g => new GitHubLogGroup(g.Name, g.Error, g.Lines))], cut, url);
    }
}