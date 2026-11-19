using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.GitHub.Host;

/// <summary>
/// The checks of many commits at once for the Git Graph: one <c>gh api graphql</c> call per 50 commits, remembered per commit so a
/// repeated look makes no call. Finished results live ten minutes and running ones 30 seconds. After a rate limit or a failed call it
/// asks nothing for a while and answers only what it holds; it logs that once and never reports an error.
/// </summary>
public sealed class GitHubCommitChecks(ToolRunner run, Action<string>? log = null, TimeProvider? time = null)
{
    public const int Batch = 50;
    private const int Names = 30;
    private static readonly TimeSpan Query = TimeSpan.FromSeconds(20);
    private static readonly long Finished = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    private static readonly long Running = (long)TimeSpan.FromSeconds(30).TotalMilliseconds;
    private static readonly long Nothing = (long)TimeSpan.FromMinutes(2).TotalMilliseconds;
    private static readonly long FirstBackOff = (long)TimeSpan.FromMinutes(2).TotalMilliseconds;
    private static readonly long LongestBackOff = (long)TimeSpan.FromMinutes(30).TotalMilliseconds;
    private static readonly Dictionary<string, string?> GhEnvironment = new() { ["GH_PROMPT_DISABLED"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["NO_COLOR"] = "1" };

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (CommitChecks? Checks, long Until)> cache = new();
    private long blockedUntil;
    private long backOff = FirstBackOff;
    private int logged;

    /// <summary>How many gh calls this has made, for the checks.</summary>
    public int Calls { get; private set; }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The checks of the commits that have any; a commit unknown to GitHub, or with no checks, is left out.</summary>
    public async ValueTask<IReadOnlyList<CommitChecks>> ReadAsync(string repository, string directory, IReadOnlyList<string> shas, CancellationToken cancellationToken)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || !parts.All(Safe)) return [];
        var wanted = shas.Where(IsSha).Distinct(StringComparer.Ordinal).ToList();
        var known = new List<CommitChecks>();
        var missing = new List<string>();
        foreach (var sha in wanted)
        {
            if (cache.TryGetValue(repository + "@" + sha, out var held) && held.Until > Now) { if (held.Checks is not null) known.Add(held.Checks); }
            else missing.Add(sha);
        }
        for (var start = 0; start < missing.Count && Now >= Volatile.Read(ref blockedUntil); start += Batch)
        {
            var batch = missing.Skip(start).Take(Batch).ToList();
            var answer = await AskAsync(repository, parts, directory, batch, cancellationToken);
            if (answer is null) break;
            foreach (var sha in batch)
            {
                var checks = answer.GetValueOrDefault(sha);
                cache[repository + "@" + sha] = (checks, Now + (checks is null ? Nothing : checks.State == "pending" ? Running : Finished));
                if (checks is not null) known.Add(checks);
            }
        }
        return known;
    }

    private async ValueTask<Dictionary<string, CommitChecks>?> AskAsync(string repository, string[] parts, string directory, List<string> batch, CancellationToken cancellationToken)
    {
        var query = new StringBuilder("query { repository(owner: \"" + parts[0] + "\", name: \"" + parts[1] + "\") {");
        for (var index = 0; index < batch.Count; index++)
            query.Append(" c").Append(index.ToString(CultureInfo.InvariantCulture)).Append(": object(oid: \"").Append(batch[index]).Append("\") { ... on Commit { statusCheckRollup { contexts(first: ")
                .Append(Names.ToString(CultureInfo.InvariantCulture)).Append(") { totalCount checkRunCountsByState { state count } statusContextCountsByState { state count } ")
                .Append("nodes { __typename ... on CheckRun { name conclusion status checkSuite { workflowRun { databaseId } } } ... on StatusContext { context state } } } } } }");
        query.Append(" } }");
        Calls++;
        try
        {
            var result = await run("gh", directory, ["api", "graphql", "-f", "query=" + query], Query, GhEnvironment, cancellationToken);
            if (result.ExitCode != 0) { Block(result.Error); return null; }
            using var json = JsonDocument.Parse(result.Output);
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object)
            {
                Block(result.Output);
                return null;
            }
            backOff = FirstBackOff;
            var found = new Dictionary<string, CommitChecks>();
            for (var index = 0; index < batch.Count; index++)
                if (repo.TryGetProperty("c" + index.ToString(CultureInfo.InvariantCulture), out var commit) && Parse(batch[index], repository, commit) is { } checks) found[batch[index]] = checks;
            return found;
        }
        catch (Exception error) when (error is Win32Exception or TimeoutException or JsonException or IOException) { Block(error.Message); return null; }
    }

    private void Block(string reason)
    {
        Volatile.Write(ref blockedUntil, Now + backOff);
        backOff = Math.Min(backOff * 2, LongestBackOff);
        if (Interlocked.Exchange(ref logged, 1) == 0)
            log?.Invoke("GitHub checks for the Git Graph are paused: " + (reason.Trim().Split('\n')[0] is { Length: > 0 } line ? line : "gh could not read GitHub") + ". They are shown again later.");
    }

    private static readonly string[] Bad = ["FAILURE", "TIMED_OUT", "STARTUP_FAILURE", "ACTION_REQUIRED", "ERROR"];
    private static readonly string[] Active = ["IN_PROGRESS", "QUEUED", "PENDING", "WAITING", "EXPECTED", "REQUESTED"];
    private static readonly string[] Good = ["SUCCESS", "NEUTRAL", "SKIPPED"];

    internal static CommitChecks? Parse(string sha, string repository, JsonElement commit)
    {
        if (commit.ValueKind != JsonValueKind.Object || !commit.TryGetProperty("statusCheckRollup", out var rollup) || rollup.ValueKind != JsonValueKind.Object) return null;
        var contexts = rollup.GetProperty("contexts");
        var total = contexts.GetProperty("totalCount").GetInt32();
        if (total == 0) return null;
        int failed = 0, active = 0, passed = 0;
        foreach (var group in new[] { "checkRunCountsByState", "statusContextCountsByState" })
            foreach (var item in contexts.GetProperty(group).EnumerateArray())
            {
                var state = item.GetProperty("state").GetString() ?? "";
                var count = item.GetProperty("count").GetInt32();
                if (Bad.Contains(state)) failed += count;
                else if (Active.Contains(state)) active += count;
                else if (Good.Contains(state)) passed += count;
            }
        var names = new List<string>();
        long? run = null, attention = null;
        foreach (var node in contexts.GetProperty("nodes").EnumerateArray())
        {
            var (name, bad, busy) = node.TryGetProperty("name", out var checkName)
                ? (checkName.GetString() ?? "", Bad.Contains(Text(node, "conclusion")), Text(node, "status") is not "COMPLETED")
                : (Text(node, "context"), Bad.Contains(Text(node, "state")), Active.Contains(Text(node, "state")));
            if (name.Length > 0 && (bad || busy)) names.Add(name);
            if (WorkflowRun(node) is { } id) { run ??= id; if (name.Length > 0 && (bad || busy)) attention ??= id; }
        }
        var overall = failed > 0 ? "failure" : active > 0 ? "pending" : passed == total ? "success" : "neutral";
        // The click goes to the run of the first check that needs a look, else to the first run; a commit without a workflow run (only statuses or other apps) gets its checks page.
        return new(sha, overall, passed, total, names, (attention ?? run) is { } runId ? $"https://github.com/{repository}/actions/runs/{runId}" : $"https://github.com/{repository}/commit/{sha}/checks");
    }

    private static long? WorkflowRun(JsonElement node) =>
        node.TryGetProperty("checkSuite", out var suite) && suite.ValueKind == JsonValueKind.Object && suite.TryGetProperty("workflowRun", out var flow) && flow.ValueKind == JsonValueKind.Object
        && flow.TryGetProperty("databaseId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && number > 0 ? number : null;

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Safe(string part) => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool IsSha(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);
}