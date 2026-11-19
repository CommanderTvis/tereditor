using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.Host;

namespace Tereditor.Checks;

/// <summary>The GitHub plugin's local cache: stale while revalidate, failures, accounts, switch, bounds and permissions, against canned gh output.</summary>
internal static partial class GitHubBrowserChecks
{
    private sealed class Gh
    {
        public string Login = "ann";
        public bool Failing;
        public string Extra = "";
        public TaskCompletionSource? Gate;
        public int Lists;
        public int Details;
        public int Started;

        public ValueTask<RepoHostToolResult> Run(string tool, string directory, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment, CancellationToken ct) =>
            RunAsync(tool, args);

        private async ValueTask<RepoHostToolResult> RunAsync(string tool, IReadOnlyList<string> args)
        {
            var text = string.Join('\n', args);
            if (tool == "git") return new(0, "git@github.com:O/R.git\n", "");
            if (args[0] == "auth") return new(0, "github.com\n  ✓ Logged in to github.com account " + Login + " (keyring)\n  - Active account: true\n", "");
            if (Failing) return new(1, "", "HTTP 401: bad credentials");
            if (args[0] == "api" && args[1] == "user") return new(0, Login + "\n", "");
            if (args[0] == "repo") return new(0, Repo, "");
            if (text.Contains("list: search", StringComparison.Ordinal))
            {
                Lists++;
                Started++;
                if (Gate is { } gate) await gate.Task;
                return new(0, ListJson(Extra), "");
            }
            if (text.Contains("issueOrPullRequest", StringComparison.Ordinal))
            {
                if (text.Contains("timelineItems", StringComparison.Ordinal)) Details++;
                return new(0, DetailJson(false, "hello"), "");
            }
            return new(1, "", "unexpected " + text);
        }
    }

    private const string ExtraItem = """,{"__typename":"Issue","number":8,"title":"New","state":"OPEN","url":"https://github.com/o/r/issues/8","createdAt":"2026-11-03T10:00:00Z","updatedAt":"2026-11-03T10:00:00Z","closedAt":null,"author":{"login":"bob"},"labels":{"nodes":[]},"comments":{"totalCount":0}}""";

    private static GitHubListParams ListOf(string? revision = null, string query = "is:open") => new("w", "issues", query, 25, revision);

    private static async Task<T> Within<T>(Task<T> task, string message)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task) throw new InvalidOperationException(message);
        return await task;
    }

    public static async Task RunCache()
    {
        var root = Path.Combine(CheckRoot.Root, "github-cache");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        string Cache(string name) => Path.Combine(root, name);
        static GitHubBrowser Browser(Gh gh, GitHubCache? cache) => new(gh.Run, cache);

        // First request without a saved copy calls gh once, answers live and stores.
        var dir = Cache("main");
        var gh = new Gh();
        var cache = new GitHubCache(dir, () => true);
        var first = await Browser(gh, cache).ListAsync(project, ListOf(), default);
        Require(first.Cached is null && first.Items.Count == 1 && !first.Unchanged, "The first request is live");
        Require(gh.Lists == 1, "The first request calls gh once");
        Require(Directory.GetFiles(dir, "*.json").Length == 1, "The first answer is stored in one file");
        Require(!Directory.EnumerateFileSystemEntries(project).Any(), "Nothing is written under the project directory");
        Require(!Directory.GetFiles(dir).Any(f => f.EndsWith(".tmp", StringComparison.Ordinal)), "No temporary file is left");

        // A new browser (a restart) answers from the saved copy before gh finishes, then the fresh answer replaces it.
        gh = new Gh { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously), Extra = ExtraItem };
        var browser = Browser(gh, new GitHubCache(dir, () => true));
        var cached = await Within(browser.ListAsync(project, ListOf(), default), "The saved copy comes back while gh is still running");
        Require(cached.Cached is { Refreshing: true, Reason: null } && cached.Items.Count == 1 && cached.Revision == first.Revision, "The saved copy is marked as refreshing: " + cached.Cached);
        Require(!gh.Gate.Task.IsCompleted, "The saved copy came back before the gh call completed");
        var again = browser.ListAsync(project, ListOf(cached.Revision), default);
        await Task.Delay(100);
        Require(!again.IsCompleted, "The second ask waits for the same gh call");
        gh.Gate.SetResult();
        var fresh = await Within(again, "The fresh answer arrives");
        Require(gh.Lists == 1, "The refresh and the second ask share one gh call, not " + gh.Lists);
        Require(fresh.Cached is null && !fresh.Unchanged && fresh.Items.Count == 2 && fresh.Revision != cached.Revision, "The fresh answer replaces the saved copy");
        var stored = await new GitHubCache(dir, () => true).ReadAsync("ann", "o/r", "list:issues", "is:open|25");
        Require(stored is not null && stored.Revision == fresh.Revision && stored.Payload.Contains("\"New\"", StringComparison.Ordinal), "The file holds the fresh answer");

        // An unchanged revision does not carry the payload again.
        gh = new Gh { Extra = ExtraItem };
        browser = Browser(gh, new GitHubCache(dir, () => true));
        var same = await browser.ListAsync(project, ListOf(fresh.Revision), default);
        Require(same.Unchanged && same.Items.Count == 0 && same.Cached is null, "An unchanged revision answers without items and without a cache note");

        // gh fails: the saved copy stays with the reason, on the first look and on the next ask.
        gh = new Gh { Failing = true };
        browser = Browser(gh, new GitHubCache(dir, () => true));
        var open = await Within(browser.ListAsync(project, ListOf(), default), "A failing gh still opens the saved copy");
        Require(open.Cached is { Refreshing: true } && open.Items.Count == 2, "The saved copy opens while gh is failing");
        var failed = await Within(browser.ListAsync(project, ListOf(open.Revision), default), "A failing gh answers");
        Require(failed.Problem is null && failed.Cached is { Refreshing: false, Reason: { Length: > 0 } reason } && reason.Contains("bad credentials", StringComparison.Ordinal) && failed.Unchanged, "A gh failure keeps the saved copy and says why: " + failed.Cached);
        var other = await browser.ListAsync(project, ListOf(null, "is:closed"), default);
        Require(other.Problem is not null && other.Cached is null, "Without a saved copy the failure is the old problem");

        // Another gh account does not see this one's copy.
        gh = new Gh { Login = "eve", Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var eve = Browser(gh, new GitHubCache(dir, () => true)).ListAsync(project, ListOf(), default);
        await Task.Delay(100);
        Require(!eve.IsCompleted && gh.Started == 1, "Another account waits for gh and gets no saved copy");
        gh.Gate.SetResult();
        Require((await eve).Cached is null, "The other account's answer is live");
        Require(Directory.GetFiles(dir, "*.json").Length == 2, "Each account has its own file");

        // The cache switched off reads and writes nothing.
        var off = Cache("off");
        gh = new Gh();
        browser = Browser(gh, new GitHubCache(off, () => false));
        await browser.ListAsync(project, ListOf(), default);
        var secondOff = await browser.ListAsync(project, ListOf(), default);
        Require(secondOff.Cached is null && gh.Lists == 2 && !Directory.Exists(off), "With the cache off every request calls gh and nothing is written");

        // A conversation round-trips too.
        var detailDir = Cache("detail");
        gh = new Gh();
        browser = Browser(gh, new GitHubCache(detailDir, () => true));
        var detail = await browser.DetailAsync(project, new("w", "pulls", 7), default);
        gh = new Gh { Failing = true };
        var savedDetail = await Within(Browser(gh, new GitHubCache(detailDir, () => true)).DetailAsync(project, new("w", "pulls", 7), default), "The saved conversation opens");
        Require(savedDetail.Cached is { Refreshing: true } && savedDetail.Timeline.Count == detail.Timeline.Count && savedDetail.Title == detail.Title && savedDetail.Revision == detail.Revision, "The saved conversation matches the live one");

        // A file that does not parse is ignored and removed; another format version is dropped.
        var file = Directory.GetFiles(dir, "*.json")[0];
        await File.WriteAllTextAsync(file, "{ not json");
        Require(await new GitHubCache(dir, () => true).ReadAsync("ann", "o/r", "list:issues", "is:open|25") is null && !File.Exists(file), "A corrupted file is ignored and removed");
        await new GitHubCache(dir, () => true).WriteAsync("ann", "o/r", "list:issues", "k", "{}", "r");
        file = Directory.GetFiles(dir, "*.json").First(f => File.ReadAllText(f).Contains("\"ann\"", StringComparison.Ordinal));
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal));
        Require(await new GitHubCache(dir, () => true).ReadAsync("ann", "o/r", "list:issues", "k") is null && !File.Exists(file), "Another format version is dropped");

        // Bounds: entries per file, age and total size, oldest first.
        var now = DateTimeOffset.UtcNow;
        var bounded = new GitHubCache(Cache("bounds"), () => true, 3, 1 << 30, TimeSpan.FromDays(30), () => now);
        for (var i = 0; i < 5; i++) { now = now.AddMinutes(1); await bounded.WriteAsync("ann", "o/r", "g", "k" + i, "{}", "r"); }
        Require(await bounded.ReadAsync("ann", "o/r", "g", "k0") is null && await bounded.ReadAsync("ann", "o/r", "g", "k1") is null && await bounded.ReadAsync("ann", "o/r", "g", "k4") is not null, "At most three requests stay, the oldest go first");
        now = now.AddDays(31);
        Require(await bounded.ReadAsync("ann", "o/r", "g", "k4") is null, "An entry older than 30 days is dropped");
        var capped = new GitHubCache(Cache("capped"), () => true, 200, 6000, TimeSpan.FromDays(30), () => now);
        var big = "\"" + new string('x', 2000) + "\"";
        for (var i = 0; i < 6; i++) { now = now.AddMinutes(1); await capped.WriteAsync("ann", "o/r", "g" + i, "k", big, "r"); }
        Require(await capped.SizeAsync() <= 6000 + 400, "The total size stays under the cap: " + await capped.SizeAsync());
        Require(await capped.ReadAsync("ann", "o/r", "g0", "k") is null && await capped.ReadAsync("ann", "o/r", "g5", "k") is not null, "The oldest entries are evicted first");

        // Owner-only permissions on Unix.
        if (!OperatingSystem.IsWindows())
        {
            Require(File.GetUnixFileMode(dir) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "The cache directory is 0700, not " + File.GetUnixFileMode(dir));
            foreach (var f in Directory.GetFiles(dir, "*.json"))
                Require(File.GetUnixFileMode(f) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "A cache file is 0600, not " + File.GetUnixFileMode(f));
        }

        // Clear removes everything and reports the size.
        var size = await cache.SizeAsync();
        Require(size > 0, "The cache has a size");
        Require(await cache.ClearAsync() == size && await cache.SizeAsync() == 0 && Directory.GetFiles(dir).Length == 0, "Clearing frees the reported size");
        Require(!Directory.EnumerateFileSystemEntries(project).Any(), "Nothing was written under the project directory");
        Console.WriteLine("PASS GitHub cache: stale while revalidate, failures, accounts, switch, corruption, bounds, permissions and clear");
    }
}