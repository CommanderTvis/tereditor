using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.Host;
using Tereditor.Plugins.GitHub.UI;

namespace Tereditor.Checks;

/// <summary>The GitHub Contributors tab: the host's statistics answers (202, 204, data, empty), the back-off, the page and its virtualized cards.</summary>
internal static class GitHubContributorsChecks
{
    private const string Repo = """{"owner":{"login":"o"},"name":"r","url":"https://github.com/o/r","hasDiscussionsEnabled":true}""";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Weeks(params (long W, int A, int D, int C)[] weeks) =>
        "[" + string.Join(',', weeks.Select(w => $$"""{"w":{{w.W}},"a":{{w.A}},"d":{{w.D}},"c":{{w.C}}}""")) + "]";

    private static string Stats(string login, string weeks) =>
        $$$"""{"total":1,"weeks":{{{weeks}}},"author":{"login":"{{{login}}}","avatar_url":"https://avatars.example/{{{login}}}","html_url":"https://github.com/{{{login}}}"}}""";

    private static GitHubBrowser Browser(List<string> calls, params string[] answers)
    {
        var next = 0;
        return new GitHubBrowser((tool, directory, args, timeout, environment, ct) =>
        {
            calls.Add(string.Join(' ', args));
            if (args[0] == "repo") return ValueTask.FromResult(new RepoHostToolResult(0, Repo, ""));
            if (args.SequenceEqual(["api", "repos/o/r"])) return ValueTask.FromResult(new RepoHostToolResult(0, """{"default_branch":"main"}""", ""));
            if (args.Contains("repos/o/r/stats/contributors")) return ValueTask.FromResult(new RepoHostToolResult(0, answers[Math.Min(next++, answers.Length - 1)], ""));
            return ValueTask.FromResult(new RepoHostToolResult(1, "", "unexpected"));
        });
    }

    private static GitHubContributors Data(int authors, int weeks)
    {
        var starts = Enumerable.Range(0, weeks).Select(i => 1_700_000_000L + i * 604800L).ToList();
        var list = Enumerable.Range(0, authors).Select(a => new GitHubContributor("user" + a, "", "https://github.com/user" + a,
            [.. Enumerable.Range(0, weeks).Select(i => (a + i) % 5)], [.. Enumerable.Range(0, weeks).Select(i => (a + i) % 7 * 10)], [.. Enumerable.Range(0, weeks).Select(i => (a + i) % 3)])).ToList();
        return new(false, "rev", "main", false, starts, list);
    }

    public static async Task Run()
    {
        var calls = new List<string>();
        var data = Weeks((1700000000, 10, 2, 3), (1700604800, 5, 1, 2));
        var browser = Browser(calls, "HTTP/2.0 202 Accepted\r\nContent-Length: 0\r\n\r\n", "HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n[" + Stats("ann", data) + "," + Stats("bob", Weeks((1700604800, 7, 7, 7))) + "]");
        var computing = await browser.ContributorsAsync("/w", new("w"), default);
        Require(computing is { Computing: true, Problem: null, Authors.Count: 0 }, "A 202 answer is Computing, not an error and not empty");
        var ready = await browser.ContributorsAsync("/w", new("w"), default);
        Require(ready is { Computing: false, Branch: "main", Problem: null } && ready.Authors.Select(a => a.Login).SequenceEqual(["ann", "bob"]) && ready.Weeks.Count == 2, "The data answer carries the default branch and the authors");
        Require(ready.Authors[1].Commits.SequenceEqual([0, 7]) && ready.Authors[0].Additions.SequenceEqual([10, 5]), "Every author's weeks line up with the shared week list");
        var statCalls = calls.Count(c => c.Contains("stats/contributors", StringComparison.Ordinal));
        var same = await browser.ContributorsAsync("/w", new("w", ready.Revision), default);
        Require(same.Unchanged && same.Authors.Count == 0 && calls.Count(c => c.Contains("stats/contributors", StringComparison.Ordinal)) == statCalls, "A known revision answers Unchanged and the answer is cached for the session");

        var none = await Browser([], "HTTP/2.0 204 No Content\r\n\r\n").ContributorsAsync("/w", new("w"), default);
        Require(none is { Computing: false, Problem: null, Authors.Count: 0 }, "A 204 answer is a plain empty result");
        var empty = await Browser([], "HTTP/2.0 200 OK\r\n\r\n[]").ContributorsAsync("/w", new("w"), default);
        Require(empty is { Computing: false, Problem: null, Authors.Count: 0 }, "An empty list is a plain empty result");

        var asked = 0;
        var tells = 0;
        var answer = await GitHubContributorsTab.FetchAsync((_, _) => Task.FromResult(++asked < 3 ? new GitHubContributors(false, "", "", true, [], []) : ready), null, [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)], () => tells++, default);
        Require(asked == 3 && tells == 2 && answer.Authors.Count == 2, "The tab asks again after each Computing answer and stops at the data");
        asked = 0;
        var gaveUp = await GitHubContributorsTab.FetchAsync((_, _) => { asked++; return Task.FromResult(new GitHubContributors(false, "", "", true, [], [])); }, null, [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)], () => { }, default);
        Require(asked == 3 && gaveUp.Computing, "The tab gives up after the last back-off step with the Computing answer");
        Require(GitHubBarChart.NiceMax(0) == 4 && GitHubBarChart.NiceMax(1632) == 2000 && GitHubBarChart.NiceMax(9) == 10 && GitHubBarChart.NiceMax(101) == 200, "The chart grid takes four even steps");
        Console.WriteLine("PASS GitHub contributors: 202, 204, empty and data answers, caching and back-off");
    }

    private static T? Find<T>(Control root) where T : Control => root.GetVisualDescendants().OfType<T>().FirstOrDefault();

    public static void RunUi()
    {
        static Task<GitHubContributors> Quiet(string? known, CancellationToken token) => Task.FromResult(new GitHubContributors(true, "", "", false, [], []));

        // Three contributors; the third has nothing in the last three months.
        var weeks = Enumerable.Range(0, 30).Select(i => 1_700_000_000L + i * 604800L).ToList();
        int[] Flat(int value, int from, int to) => [.. Enumerable.Range(0, 30).Select(i => i >= from && i < to ? value : 0)];
        var three = new GitHubContributors(false, "r", "main", false, weeks,
        [
            new("ann", "", "https://github.com/ann", Flat(5, 0, 30), Flat(100, 0, 30), Flat(10, 0, 30)),
            new("bob", "", "https://github.com/bob", Flat(1, 25, 30), Flat(1, 25, 30), Flat(1, 25, 30)),
            new("cy", "", "https://github.com/cy", Flat(9, 0, 10), Flat(9, 0, 10), Flat(9, 0, 10))
        ]);
        var tab = new GitHubContributorsTab(null, "w", Quiet);
        tab.Show(three);
        var model = tab.Model;
        var ranked = model.Ranked();
        Require(ranked.Select(r => (r.Author.Login, r.Rank)).SequenceEqual([("ann", 1), ("bob", 2)]) && ranked[0] is { Commits: 65, Additions: 1300, Deletions: 130 }, "A contributor with nothing in the period is left out and the rest are ranked");
        model.SetPeriod("All time");
        Require(model.Ranked().Select(r => r.Author.Login).SequenceEqual(["ann", "cy", "bob"]), "A longer period brings the contributor back");
        model.SetMetric(ContributionMetric.Deletions);
        Require(model.Totals(ContributionMetric.Commits)[0] == 14 && model.Ranked()[0].Author.Login == "ann", "The metric orders the cards and totals sum every author's week");
        model.SetPeriod("Last 3 months");
        Require(model.Start == 17 && model.End == 30, "Three months is thirteen weeks ending at the newest week");

        var window = new Window { Content = tab, Width = 1100, Height = 900 };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Require(Find<GitHubBarChart>(tab) is not null && Find<GitHubRangeSelector>(tab) is not null && tab.GetVisualDescendants().OfType<GitHubContributorCard>().Count() == 2, "The page has the big chart, the range selector and a card per contributor in the period");
        foreach (var chart in tab.GetVisualDescendants().OfType<GitHubBarChart>())
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)chart.Bounds.Width), Math.Max(1, (int)chart.Bounds.Height)));
            bitmap.Render(chart);
        }
        window.Close();

        foreach (var (empty, text) in new[] { (new GitHubContributors(false, "e", "main", false, [], []), "no contribution statistics"), (new GitHubContributors(false, "", "", true, [], []), "still computing") })
        {
            var plain = new GitHubContributorsTab(null, "w", Quiet);
            plain.Show(empty);
            Require(plain.Child is TextBlock { Text: { } shown } && shown.Contains(text, StringComparison.Ordinal), "An empty or unfinished answer is a plain message");
        }
        var waiting = new GitHubContributorsTab(null, "w", Quiet);
        waiting.ShowComputing();
        Require(waiting.Child is TextBlock { Text: "GitHub is computing these numbers..." }, "The tab says GitHub is computing while it waits");

        var big = new GitHubContributorsTab(null, "w", Quiet);
        big.Show(Data(5000, 60));
        var host = new Window { Content = big, Width = 1100, Height = 900 };
        host.Show(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        var live = big.GetVisualDescendants().OfType<GitHubContributorCard>().Count();
        Require(big.Model.Ranked().Count == 5000 && live is > 0 and <= 40, "Five thousand contributors keep only the visible cards as controls (" + live + ")");
        host.Close();

        for (var i = 0; i < 8; i++) GitHubAvatarCache.Add("https://a.example/" + i, new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888), 3L << 20);
        Require(GitHubAvatarCache.Used <= GitHubAvatarCache.Budget && GitHubAvatarCache.TryGet("https://a.example/0") is null && GitHubAvatarCache.TryGet("https://a.example/7") is not null, "The avatar cache drops the least recently used picture past 16 MB");
        Require(GitHubAvatarCache.Allowed("https://avatars.githubusercontent.com/u/1") && GitHubAvatarCache.Allowed("http://x.example/a.png") && !GitHubAvatarCache.Allowed("file:///etc/passwd") && !GitHubAvatarCache.Allowed("ftp://x/a") && !GitHubAvatarCache.Allowed("https://user:pw@x.example/a"), "Avatars load over http and https only, without credentials");
        Console.WriteLine("PASS GitHub contributors page: ranking, period, empty states and virtualized cards");
    }
}