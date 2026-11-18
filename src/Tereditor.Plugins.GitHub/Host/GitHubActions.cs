using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tereditor.Plugins.GitHub.Host;

/// <summary>Posting comments and closing or reopening, and reading one job's log. Only a click in the UI reaches PostAsync.</summary>
public sealed partial class GitHubBrowser
{
    private const int LogStepLines = 5000;
    private const int LogTotalLines = 20000;
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
            var log = ParseLog(result.Output, url, StepStarts(job.RootElement));
            if (logs.Count > 40) logs.Clear();
            logs[p.JobId] = log;
            return log;
        }
        catch (Exception error) when (IsExpected(error)) { return new([], 0, "", Describe(error)); }
    }

    [GeneratedRegex(@"^(\d{4}-\d\d-\d\d)T(\d\d:\d\d:\d\d)[.\d]*Z ?")]
    private static partial Regex Stamp();

    /// <summary>A step as the jobs API lists it: its number, name and start (second precision, ISO).</summary>
    public readonly record struct StepStart(int Number, string Name, string Started);

    private sealed class StepBuilder(int number, string name, DateTimeOffset? started)
    {
        public int Number { get; } = number;
        public string Name { get; } = name;
        public DateTimeOffset? Started { get; } = started;
        public bool Error { get; set; }
        public List<GitHubLogLine> Lines { get; } = [];
    }

    private static List<StepStart> StepStarts(JsonElement job)
    {
        var list = new List<StepStart>();
        if (job.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            foreach (var step in steps.EnumerateArray()) list.Add(new(step.GetProperty("number").GetInt32(), Text(step, "name"), Text(step, "started_at")));
        return list;
    }

    /// <summary>
    /// Splits a job log into its steps and keeps it bounded. A line belongs to the last step that started at or before the line's time
    /// (steps carry second precision, the log finer); lines with no time stay with the step before. Colour codes stay in the text.
    /// </summary>
    public static GitHubLog ParseLog(string text, string url, IReadOnlyList<StepStart>? starts = null)
    {
        var steps = (starts is { Count: > 0 } ? starts : [new StepStart(0, "", "")])
            .Select(s => new StepBuilder(s.Number, s.Name, DateTimeOffset.TryParse(s.Started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null))
            .OrderBy(s => s.Started ?? DateTimeOffset.MinValue).ThenBy(s => s.Number).ToList();
        var current = steps[0];
        var inGroup = false;
        foreach (var raw in text.Replace(((char)0xFEFF).ToString(), "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var time = "";
            if (Stamp().Match(line) is { Success: true } stamp)
            {
                time = stamp.Groups[2].Value;
                line = line[stamp.Length..];
                if (steps.Count > 1 && DateTimeOffset.TryParse(stamp.Groups[1].Value + "T" + time + "Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                    current = steps.LastOrDefault(s => s.Started is { } started && started <= at) ?? steps[0];
            }
            if (line.StartsWith("##[endgroup]", StringComparison.Ordinal)) { inGroup = false; continue; }
            var header = line.StartsWith("##[group]", StringComparison.Ordinal);
            if (header) { line = line["##[group]".Length..]; inGroup = true; }
            var error = line.StartsWith("##[error]", StringComparison.Ordinal);
            if (error) { line = line["##[error]".Length..]; current.Error = true; }
            if (line.Length == 0 && time.Length == 0) continue;
            current.Lines.Add(new GitHubLogLine(time, line) { Header = header, Grouped = inGroup && !header, Error = error });
        }
        var cut = 0;
        var kept = steps.OrderBy(s => s.Number).Select(s => (Step: s, Lines: s.Lines, Cut: 0)).ToList();
        for (var i = 0; i < kept.Count; i++)
        {
            var lines = kept[i].Lines;
            if (lines.Count > LogStepLines) { var drop = lines.Count - LogStepLines; cut += drop; kept[i] = (kept[i].Step, lines.GetRange(drop, LogStepLines), drop); }
        }
        var total = kept.Sum(s => s.Lines.Count);
        for (var i = 0; total > LogTotalLines && i < kept.Count; i++)
        {
            var drop = Math.Min(kept[i].Lines.Count, total - LogTotalLines);
            kept[i] = (kept[i].Step, kept[i].Lines.GetRange(drop, kept[i].Lines.Count - drop), kept[i].Cut + drop);
            total -= drop; cut += drop;
        }
        return new([.. kept.Select(s => new GitHubLogStep(s.Step.Number, s.Step.Name, s.Lines, s.Step.Error, s.Cut))], cut, url);
    }
}