using System.Diagnostics;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core;

internal static class GitRepository
{
    /// <summary>Upstream's budget for any Git call: long enough for a slow fetch, short enough to report one that stalled.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(55);
    private const int ErrorLimit = 2000, ErrorHead = 1200;
    private const string Truncated = "… (truncated) …";

    private static readonly Dictionary<string, string?> Prompts = new()
    {
        ["LC_ALL"] = "C",
        // Background status refreshes must not take index.lock away from the user's own Git commands.
        ["GIT_OPTIONAL_LOCKS"] = "0",
        // With no terminal to ask on, a credential or passphrase prompt fails at once instead of waiting.
        ["GIT_TERMINAL_PROMPT"] = "0"
    };

    /// <summary>How many Git runs were started, for checks that assert a missing project is not polled.</summary>
    internal static long Invocations;

    /// <summary>Non-zero Git exits by command, as results and as exceptions, for checks that name what an idle host asks Git.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> NonZeroExits = new();

    /// <summary>How many of those non-zero exits were raised as exceptions; a probe hands its exit back and raises nothing.</summary>
    internal static long Raised;

    private static void CountExit(string[] args, int exitCode)
    {
        var key = $"git {string.Join(' ', args.Take(4))} => {exitCode}";
        NonZeroExits.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    /// <summary>A Git run whose non-zero exit is an answer the caller reads, not a failure.</summary>
    internal readonly record struct Probe(int ExitCode, string Output, string Error)
    {
        internal bool Succeeded => ExitCode == 0;
    }

    /// <summary>
    /// Runs Git and hands back its exit code. A quiet probe (<c>--quiet</c>, <c>config --get</c>, a ref that may be absent)
    /// exits 1 as its normal "no"; that is a result here, not an exception. A run that timed out or never started still throws.
    /// </summary>
    internal static async Task<Probe> ProbeAsync(string root, CancellationToken ct, params string[] args)
    {
        var result = await RunCoreAsync(root, args, Budget, ct);
        return new(result.ExitCode, result.Output, result.Error);
    }

    /// <summary>Standard output when Git exits 0, null on any other exit; for callers that treat every failure as "not there".</summary>
    internal static async Task<string?> TryRunAsync(string root, CancellationToken ct, params string[] args)
    {
        var probe = await ProbeAsync(root, ct, args);
        return probe.Succeeded ? probe.Output : null;
    }

    /// <summary>The exception a failed run raises, for callers that read a probe and find the exit is a real error.</summary>
    internal static IOException Failure(Probe probe) => Raise(probe.ExitCode, Bounded(probe.Error));

    private static IOException Raise(int exitCode, string message)
    {
        Interlocked.Increment(ref Raised);
        return new GitException(exitCode, message);
    }

    internal static async Task<string> RunAsync(string root, CancellationToken ct, params string[] args) =>
        (await ExecuteAsync(root, args, Budget, ct)).Output;

    /// <summary>Runs Git and returns its standard output undecoded, for blob contents whose bytes are hashed.</summary>
    internal static async Task<byte[]> RunBytesAsync(string root, CancellationToken ct, params string[] args) =>
        (await ExecuteAsync(root, args, Budget, ct)).Bytes;

    internal static async Task<ChildProcess.Result> ExecuteAsync(string root, string[] args, TimeSpan budget, CancellationToken ct)
    {
        var result = await RunCoreAsync(root, args, budget, ct);
        if (result.ExitCode != 0) throw Raise(result.ExitCode, Bounded(result.Error));
        return result;
    }

    private static async Task<ChildProcess.Result> RunCoreAsync(string root, string[] args, TimeSpan budget, CancellationToken ct)
    {
        Interlocked.Increment(ref Invocations);
        string[] full = ["--literal-pathspecs", "-c", "core.quotepath=false", .. args];
        ChildProcess.Result result;
        try { result = await ChildProcess.RunAsync("git", root, full, budget, ct, Prompts); }
        catch (ChildProcess.ExpiredException error) { throw Raise(-1, TimeoutMessage(error, args)); }
        if (result.ExitCode != 0) CountExit(args, result.ExitCode);
        return result;
    }

    /// <summary>Keeps what Git wrote; without it, names only what was observed, with the SSH hint for network calls alone.</summary>
    private static string TimeoutMessage(ChildProcess.ExpiredException error, string[] args)
    {
        var network = args.Length > 0 && args[0] is "fetch" or "push" or "pull" or "clone" or "ls-remote";
        var captured = Bounded(error.Error);
        if (captured.Length == 0)
            captured = network
                ? "the remote never answered; if it uses SSH, a key that is not loaded is the usual cause (`ssh-add`)"
                : "git did not exit";
        return $"timed out after {Math.Max(1, Math.Round(error.Waited.TotalSeconds)):0}s — {captured}";
    }

    private static string Bounded(string raw)
    {
        var text = raw.Trim();
        return text.Length <= ErrorLimit ? text : text[..ErrorHead] + Truncated + text[^(ErrorLimit - Truncated.Length - ErrorHead)..];
    }

    /// <summary>The two sides of a file's diff: null is absent, an empty string the index, otherwise a commit id; a null modified side is the working tree.</summary>
    internal sealed record DiffRange(string? Original, string? Modified);

    /// <summary>Resolves a scope to the exact revisions both sides are read at, so a diff and a revert of it agree.</summary>
    internal static async Task<DiffRange> ResolveDiffRangeAsync(string root, string path, string scope, string comparison, CancellationToken ct)
    {
        async Task<string?> Head()
        {
            return (await TryRunAsync(root, ct, "rev-parse", "--verify", "--quiet", "HEAD"))?.Trim();
        }
        async Task<bool> Untracked() =>
            (await RunAsync(root, ct, "ls-files", "--others", "--exclude-standard", "-z", "--", path)).Length > 0;
        if (scope == "untracked" || (scope is "uncommitted" or "branch" or "pinned") && await Untracked()) return new(null, null);
        switch (scope)
        {
            case "commit":
                var (parent, commit) = await CommitRangeAsync(root, comparison, ct);
                return new(parent, commit);
            case "staged": return new(await Head(), "");
            case "working": return new("", null);
            case "branch": return new(await ComparisonBaseAsync(root, comparison, ct), null);
            case "pinned": return new(await PinnedCommitAsync(root, comparison, ct), null);
            case "uncommitted": return new(await Head(), null);
            default: return new(await Head() ?? "", null);
        }
    }

    /// <summary>
    /// The runner plugins reach (H17): the same environment as the host's own reads, a bounded run, and network
    /// transports refused unless asked for. A non-zero exit is a result, not an exception.
    /// </summary>
    internal static Task<GitRunResult> RunBoundedAsync(string cwd, IReadOnlyList<string> args, GitRunOptions? options, CancellationToken ct) =>
        RunBoundedAsync(cwd, args, options, ct, null);

    internal static async Task<GitRunResult> RunBoundedAsync(string cwd, IReadOnlyList<string> args, GitRunOptions? options, CancellationToken ct, string? input)
    {
        Interlocked.Increment(ref Invocations);
        if (GitBackends.Current is { } backend)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(options?.Timeout ?? TimeSpan.FromSeconds(30));
            try
            {
                var done = await backend.RunAsync(cwd, args, input is null ? null : System.Text.Encoding.UTF8.GetBytes(input), budget.Token);
                return new(done.ExitCode == 0, System.Text.Encoding.UTF8.GetString(done.Output), done.Error);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "", "", GitRunFailure.Timeout); }
        }
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["LC_ALL"] = "C";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (options?.Network != true) start.Environment["GIT_ALLOW_PROTOCOL"] = "file";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(options?.Timeout ?? TimeSpan.FromSeconds(30));
        var trace = new GitRunTrace(args);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Could not start git."); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            return new(false, "", error.Message, GitRunFailure.Launch);
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            trace.Mark("spawn");
            using var registration = bounded.Token.Register(() =>
            {
                trace.Mark("kill");
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            try
            {
                if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), bounded.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
            }
            var exit = process.WaitForExitAsync(CancellationToken.None).ContinueWith(_ => trace.Mark("exit"), TaskScheduler.Default);
            var finished = Task.WhenAll(exit, output, error);
            try
            {
                try { await finished.WaitAsync(bounded.Token); }
                // Killed: the tree gets a short grace, since an orphan that inherited the pipes can keep them open indefinitely.
                catch (OperationCanceledException) when (bounded.IsCancellationRequested) { await finished.WaitAsync(KillGrace, CancellationToken.None); }
            }
            catch (TimeoutException)
            {
                trace.Report("abandoned");
                ct.ThrowIfCancellationRequested();
                return new(false, "", "git did not exit after it was stopped.", GitRunFailure.Timeout);
            }
            trace.Mark("read");
            trace.Report(bounded.IsCancellationRequested ? "killed" : "done");
            var (text, detail) = (await output, await error);
            ct.ThrowIfCancellationRequested();
            if (bounded.IsCancellationRequested) return new(false, text, detail, GitRunFailure.Timeout);
            return new(process.ExitCode == 0, text, detail);
        }
    }

    /// <summary>How long a killed Git run may take to release its pipes before the run is abandoned.</summary>
    internal static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    /// <summary>Runs slower than this write one stderr line of their phase times, so a stalled child is visible in logs.</summary>
    internal static readonly TimeSpan SlowRun = TimeSpan.FromSeconds(5);

    // Phase offsets of one bounded run; silent unless the run was slow. Arguments only, never stdin or output.
    private sealed class GitRunTrace(IReadOnlyList<string> args)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DateTime started = DateTime.UtcNow;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> marks = new();
        internal void Mark(string phase) => marks.Enqueue($"{phase}=+{clock.ElapsedMilliseconds}ms");
        internal void Report(string outcome)
        {
            if (clock.Elapsed < SlowRun) return;
            Console.Error.WriteLine($"Trace slow git run ({outcome}): start={started:O} {string.Join(' ', marks)} total={clock.ElapsedMilliseconds}ms args=[{string.Join(' ', args)}]");
        }
    }

    private sealed class GitException(int exitCode, string message) : IOException(message)
    {
        internal int ExitCode { get; } = exitCode;
    }

    /// <summary>The repository's shared Git directory, which every linked worktree resolves to; null outside a repository.</summary>
    internal static async Task<string?> CommonDirectoryAsync(string root, CancellationToken ct)
    {
        var path = (await TryRunAsync(root, ct, "rev-parse", "--git-common-dir"))?.TrimEnd('\r', '\n');
        return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path, root);
    }

    /// <summary>Whether Git tracks anything at or under <paramref name="path"/>, a root-relative file or directory.</summary>
    internal static async Task<bool> IsTrackedAsync(string root, string path, CancellationToken ct) =>
        (await RunAsync(root, ct, "ls-files", "-z", "--", path)).Length > 0;

    /// <summary>Whether the ignore rules (.gitignore files, info/exclude and the user's excludes file) cover <paramref name="path"/>.</summary>
    internal static async Task<bool> IsIgnoredAsync(string root, string path, CancellationToken ct)
    {
        // check-ignore refuses --literal-pathspecs, which every other call sets, so it runs without it.
        ChildProcess.Result result;
        try { result = await ChildProcess.RunAsync("git", root, ["-c", "core.quotepath=false", "check-ignore", "-q", "--", path], Budget, ct, Prompts); }
        catch (ChildProcess.ExpiredException error) { throw new GitException(-1, TimeoutMessage(error, ["check-ignore"])); }
        // Exit 1 is "not ignored"; anything else is a real failure.
        return result.ExitCode switch { 0 => true, 1 => false, _ => throw new GitException(result.ExitCode, Bounded(result.Error)) };
    }

    internal static async Task<string> ComparisonBaseAsync(string root, string comparison, CancellationToken ct)
    {
        var target = (await RunAsync(root, ct, "rev-parse", "--verify", "--end-of-options", GitRefs.Require(comparison) + "^{commit}")).Trim();
        // Exit 1 is "no common ancestor": the target itself is the base.
        var probe = await ProbeAsync(root, ct, "merge-base", "--end-of-options", target, "HEAD");
        return probe.ExitCode switch { 0 => probe.Output.Trim(), 1 => target, _ => throw Failure(probe) };
    }

    internal static async Task<List<string>> CommitDiffArgumentsAsync(string root, string commit, CancellationToken ct)
    {
        var (parent, sha) = await CommitRangeAsync(root, commit, ct);
        return parent is null ? ["show", "--format=", "--no-renames", sha] : ["diff", "--no-renames", parent, sha];
    }

    /// <summary>
    /// The immutable commit a pinned scope measures the working tree against. Unlike a branch target it is used
    /// as given, never through a merge base, so the range cannot move while the branch or its target does.
    /// </summary>
    internal static async Task<string> PinnedCommitAsync(string root, string commit, CancellationToken ct)
    {
        RequireCommitId(commit);
        var probe = await ProbeAsync(root, ct, "rev-parse", "--verify", "--quiet", "--end-of-options", commit + "^{commit}");
        return probe.ExitCode switch
        {
            0 => probe.Output.Trim(),
            1 => throw new HostException(HostErrorCode.UnknownCommit, $"Unknown commit: {commit}"),
            _ => throw Failure(probe)
        };
    }

    private static void RequireCommitId(string commit)
    {
        if (commit.Length is < 4 or > 64 || !commit.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("A commit scope requires a hexadecimal commit id.");
    }

    /// <summary>Resolves a commit and its first parent, which is null for a root commit.</summary>
    internal static async Task<(string? Parent, string Commit)> CommitRangeAsync(string root, string commit, CancellationToken ct)
    {
        var sha = await PinnedCommitAsync(root, commit, ct);
        var probe = await ProbeAsync(root, ct, "rev-parse", "--verify", "--quiet", "--end-of-options", sha + "^");
        return probe.ExitCode switch { 0 => (probe.Output.Trim(), sha), 1 => (null, sha), _ => throw Failure(probe) };
    }

    internal static async Task<GitSnapshot> SnapshotAsync(string root, string comparison, CancellationToken ct, string scope = "all")
    {
        if (scope is not ("all" or "uncommitted" or "staged" or "commit" or "pinned")) throw new ArgumentException("Unknown change scope.");
        var gitDir = await ProbeAsync(root, ct, "rev-parse", "--git-dir");
        if (!gitDir.Succeeded)
        {
            if (gitDir.ExitCode == 128 && gitDir.Error.Trim().StartsWith("fatal: not a git repository (or any", StringComparison.Ordinal))
                return new(false, "", [], [], []);
            throw Failure(gitDir);
        }
        var head = await ProbeAsync(root, ct, "symbolic-ref", "--short", "-q", "HEAD");
        if (head.ExitCode is not (0 or 1)) throw Failure(head);
        var branch = head.Output.Trim();
        if (string.IsNullOrEmpty(branch)) branch = "detached HEAD";
        var status = "";
        if (scope != "commit")
        {
            var run = await ProbeAsync(root, ct, "status", "--porcelain=v1", "-z", "--untracked-files=all");
            // A bare repository has no files to show: it reads as no repository, not as an error on every refresh.
            if (run.ExitCode == 128 && run.Error.Contains("work tree", StringComparison.Ordinal)) return new(false, "", [], [], []);
            if (!run.Succeeded) throw Failure(run);
            status = run.Output;
        }
        var workingChanges = ParseStatus(status);
        var diff = scope == "commit" ? await CommitDiffArgumentsAsync(root, comparison, ct) : new List<string> { "diff", "--no-renames" };
        if (scope == "staged") diff.Add("--cached");
        else if (scope == "pinned") diff.Add(await PinnedCommitAsync(root, comparison, ct));
        else if (scope == "all" && comparison.Length > 0)
            diff.Add(await ComparisonBaseAsync(root, comparison, ct));
        else if (scope == "uncommitted") diff.Add("HEAD");
        else if (scope == "all")
        {
            var born = await ProbeAsync(root, ct, "rev-parse", "--verify", "--quiet", "HEAD");
            if (born.ExitCode is not (0 or 1)) throw Failure(born);
            diff.Add(born.Succeeded ? "HEAD" : "--cached");
        }
        var names = await RunAsync(root, ct, diff.Concat(new[] { "--name-status", "-z", "--" }).ToArray());
        var statuses = workingChanges.ToDictionary(change => change.Path, StringComparer.Ordinal);
        var changes = ParseNames(names).Select(change => statuses.GetValueOrDefault(change.Path, change)).ToList();
        if (scope != "staged") changes.AddRange(workingChanges.Where(change => change.IndexStatus == "?"));
        var stats = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        var stat = await RunAsync(root, ct, diff.Concat(new[] { "--numstat", "-z", "--" }).ToArray());
        foreach (var row in stat.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = row.Split('\t', 3);
            if (fields.Length != 3) continue;
            stats[fields[2]] = (int.TryParse(fields[0], out var add) ? add : 0,
                int.TryParse(fields[1], out var remove) ? remove : 0);
        }
        changes = changes.Select(change => stats.TryGetValue(change.Path, out var value)
            ? change with { Added = value.Added, Removed = value.Removed } : change).ToList();
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            if (change.IndexStatus != "?") continue;
            var path = Path.Combine(root, change.Path);
            try
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null || info.Length > 8 * 1024 * 1024) continue;
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (!ContentClassifier.IsText(bytes)) continue;
                var lines = bytes.Count(value => value == '\n');
                if (bytes.Length > 0 && bytes[^1] != '\n') lines++;
                changes[index] = change with { Added = lines };
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var worktrees = await ListWorktreesAsync(root, ct);
        var branches = (await RunAsync(root, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Commit and pinned scopes carry a commit id where the others carry the target the catalog is listed against.
        var commits = scope is "commit" or "pinned" ? [] : await ListCommitsAsync(root, comparison, ct);
        return new(true, branch, changes, worktrees, branches) { Commits = commits };
    }

    internal static async Task<GitCommit?> GetCommitAsync(string root, string sha, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sha.Length is < 4 or > 64 || !sha.All(char.IsAsciiHexDigit)) throw new ArgumentException("A commit lookup requires a hexadecimal id.");
        var found = await ProbeAsync(root, ct, "rev-parse", "--verify", "--quiet", "--end-of-options", sha + "^{commit}");
        if (found.ExitCode == 1) return null;
        if (!found.Succeeded) throw Failure(found);
        var commit = found.Output.Trim();
        var log = await RunAsync(root, ct, "show", "-s", "--format=%H%x00%h%x00%cI%x00%an%x00%s", "--end-of-options", commit, "--");
        var fields = log.TrimEnd('\r', '\n').Split('\0', 5);
        return fields.Length == 5 ? new(fields[0], fields[1], DisplayText(fields[4]), DisplayText(fields[3]), fields[2]) : null;
    }

    internal static async Task<IReadOnlyList<GitCommit>> ListCommitsAsync(string root, string comparison, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var commits = new List<GitCommit>();
        if (comparison.Length > 0)
        {
            GitRefs.Require(comparison);
            // Exit 128 is a branch with no commits yet or a comparison that does not exist: no commits to list.
            var listed = await ProbeAsync(root, ct, "log", "--max-count=200", "--format=%H%x00%h%x00%cI%x00%an%x00%s", "--end-of-options", comparison + "..HEAD", "--");
            if (listed.ExitCode is not (0 or 128)) throw Failure(listed);
            var log = listed.Succeeded ? listed.Output : "";
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\0', 5);
                if (fields.Length == 5) commits.Add(new(fields[0], fields[1], DisplayText(fields[4]), DisplayText(fields[3]), fields[2]));
            }
        }
        return commits;
    }

    private static string DisplayText(string text) => new(text.Where(value =>
        value is not (< '\u0020' or >= '\u007f' and <= '\u009f' or >= '\u200b' and <= '\u200f' or
            >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069' or '\u061c' or '\ufeff' or '\u00ad')).ToArray());

    private static List<GitChange> ParseStatus(string status)
    {
        var parts = status.Split('\0');
        var result = new List<GitChange>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length < 4) continue;
            var row = parts[i];
            string? original = null;
            if (row[0] is 'R' or 'C' || row[1] is 'R' or 'C') original = parts[++i];
            result.Add(new(row[3..], row[..1], row[1..2], original, 0, 0));
        }
        return result;
    }

    private static List<GitChange> ParseNames(string names)
    {
        var parts = names.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<GitChange>();
        for (var i = 0; i + 1 < parts.Length; i += 2) result.Add(new(parts[i + 1], " ", parts[i], null, 0, 0));
        return result;
    }

    internal static async Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string root, CancellationToken ct) =>
        [.. ParseWorktrees(await RunAsync(root, ct, "worktree", "list", "--porcelain", "-z")).Where(tree => Directory.Exists(tree.Path))];

    /// <summary>The worktrees still on disk, or null when the folder is no repository.</summary>
    internal static async Task<IReadOnlyList<WorktreeInfo>?> TryListWorktreesAsync(string root, CancellationToken ct) =>
        await TryRunAsync(root, ct, "worktree", "list", "--porcelain", "-z") is { } text
            ? [.. ParseWorktrees(text).Where(tree => Directory.Exists(tree.Path))] : null;

    internal static List<WorktreeInfo> ParseWorktrees(string text)
    {
        var result = new List<WorktreeInfo>();
        string path = "", branch = "";
        var locked = false;
        foreach (var line in text.Split('\0'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..];
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal)) branch = line[18..];
            else if (line == "detached") branch = "detached HEAD";
            else if (line.StartsWith("locked", StringComparison.Ordinal)) locked = true;
            else if (line.Length == 0 && path.Length > 0)
            {
                result.Add(new(ProjectPaths.DirectoryIdentity(path), branch, result.Count == 0, locked));
                path = ""; branch = ""; locked = false;
            }
        }
        return result;
    }
}