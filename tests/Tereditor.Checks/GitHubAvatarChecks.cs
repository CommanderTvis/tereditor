using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.Host;
using Tereditor.Plugins.GitHub.UI;

namespace Tereditor.Checks;

/// <summary>Assignee avatars: the host's disk store (one fetch, restart, bounds, clear) and the row's circles (initial first, the image in place, no second request).</summary>
internal static partial class GitHubBrowserChecks
{
    private const string AvatarUrl = "https://avatars.githubusercontent.com/u/1?u=abc&v=4&s=48";

    private static byte[] FakeImage(int size = 200)
    {
        var bytes = new byte[size];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    public static async Task RunAvatars()
    {
        var root = Path.Combine(CheckRoot.Root, "github-avatars");
        var requested = new List<Uri>();
        Task<byte[]?> Fetch(Uri uri, CancellationToken _) { lock (requested) requested.Add(uri); return Task.FromResult<byte[]?>(FakeImage()); }
        int Requests() { lock (requested) return requested.Count; }

        // One fetch for an address however it is spelled; the saved copy answers after a restart.
        var dir = Path.Combine(root, "main");
        var store = new GitHubAvatarStore(dir, () => true, Fetch);
        var first = await Within(store.GetAsync(AvatarUrl, default), "The first avatar arrives");
        var again = await Within(store.GetAsync("https://avatars.githubusercontent.com/u/1?v=5&s=460", default), "The same avatar again arrives");
        Require(first is { Length: 200 } && again is { Length: 200 } && Requests() == 1, "A second ask for the same avatar, with other query parts, makes no request: " + Requests());
        Require(requested[0].Query == "?s=48" && requested[0].Host == "avatars.githubusercontent.com", "The request is the 48 pixel variant without volatile query parts: " + requested[0]);
        Require(Directory.GetFiles(dir, "*.img").Length == 1, "The image is saved on disk");
        var restarted = new GitHubAvatarStore(dir, () => true, Fetch);
        Require(await Within(restarted.GetAsync(AvatarUrl, default), "The saved avatar arrives") is { Length: 200 } && Requests() == 1, "A new host instance reads the avatar from disk without a request");
        Require(await store.GetAsync("http://avatars.githubusercontent.com/u/1", default) is null && await store.GetAsync("https://example.com/a.png", default) is null && await store.GetAsync("", default) is null && Requests() == 1,
            "Only https addresses on githubusercontent.com are fetched");
        var junk = new GitHubAvatarStore(Path.Combine(root, "junk"), () => true, (_, _) => Task.FromResult<byte[]?>("<html>not an image at all</html>"u8.ToArray()));
        Require(await junk.GetAsync(AvatarUrl, default) is null && !Directory.Exists(Path.Combine(root, "junk")), "Bytes that are no image are neither served nor saved");
        if (!OperatingSystem.IsWindows())
        {
            Require(File.GetUnixFileMode(dir) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "The avatar directory is 0700");
            foreach (var f in Directory.GetFiles(dir, "*.img")) Require(File.GetUnixFileMode(f) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "An avatar file is 0600");
        }
        Require(Directory.GetFiles(dir, "*.tmp").Length == 0, "No temporary file stays");

        // Many asks at once share one request.
        var gate = new TaskCompletionSource();
        var slow = 0;
        var shared = new GitHubAvatarStore(Path.Combine(root, "shared"), () => true, async (_, _) => { Interlocked.Increment(ref slow); await gate.Task; return FakeImage(); });
        var asks = Enumerable.Range(0, 10).Select(_ => shared.GetAsync(AvatarUrl, default)).ToList();
        gate.SetResult();
        await Within(Task.WhenAll(asks), "Shared asks finish");
        Require(Volatile.Read(ref slow) == 1, "Ten asks at the same time make one request: " + slow);

        // Bounds: entries, then bytes; the least recently used goes first.
        var bounded = new GitHubAvatarStore(Path.Combine(root, "bounded"), () => true, Fetch, maxEntries: 3);
        var start = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
        {
            await Within(bounded.GetAsync("https://avatars.githubusercontent.com/u/" + (100 + i), default), "A bounded avatar arrives");
            var newest = new DirectoryInfo(Path.Combine(root, "bounded")).GetFiles("*.img").OrderByDescending(f => f.LastWriteTimeUtc).First();
            newest.LastWriteTimeUtc = start.AddMinutes(i);
        }
        var kept = Directory.GetFiles(Path.Combine(root, "bounded"), "*.img").Length;
        Require(kept == 3, "At most three avatars stay: " + kept);
        var before = Requests();
        await Within(bounded.GetAsync("https://avatars.githubusercontent.com/u/104", default), "The newest avatar arrives");
        Require(Requests() == before, "The newest avatar is still saved");
        await Within(bounded.GetAsync("https://avatars.githubusercontent.com/u/100", default), "The oldest avatar arrives again");
        Require(Requests() == before + 1, "The oldest avatar was evicted and is fetched again");
        var capped = new GitHubAvatarStore(Path.Combine(root, "capped"), () => true, Fetch, maxBytes: 450);
        for (var i = 0; i < 5; i++) await Within(capped.GetAsync("https://avatars.githubusercontent.com/u/" + (200 + i), default), "A capped avatar arrives");
        Require(await capped.SizeAsync() <= 450, "The total size stays under the cap: " + await capped.SizeAsync());

        // The cache switch: nothing is saved while it is off.
        var off = new GitHubAvatarStore(Path.Combine(root, "off"), () => false, Fetch);
        Require(await Within(off.GetAsync(AvatarUrl, default), "An avatar arrives with the cache off") is { Length: 200 } && !Directory.Exists(Path.Combine(root, "off")), "With the cache off the avatar is served and not saved");

        // Clear cache (the browser's size and clear) covers the avatars.
        var cacheDir = Path.Combine(root, "browser");
        var browser = new GitHubBrowser(new Gh().Run, new GitHubCache(cacheDir, () => true), new GitHubAvatarStore(Path.Combine(cacheDir, "avatars"), () => true, Fetch));
        await Within(browser.AvatarAsync(AvatarUrl, default), "The browser serves an avatar");
        var size = await browser.CacheSizeAsync();
        Require(size >= 200, "The cache size counts the avatars: " + size);
        Require(await browser.ClearCacheAsync() == size && await browser.CacheSizeAsync() == 0 && Directory.GetFiles(Path.Combine(cacheDir, "avatars")).Length == 0, "Clear cache removes the saved avatars");
        Console.WriteLine("PASS GitHub avatars: one fetch per address, disk copy after a restart, address rules, bounds, switch and clear cache");
    }

    /// <summary>The circle holds a decoded image that is laid out inside it and visible.</summary>
    private static bool PaintedRed(Window window, Border circle) =>
        GitHubAvatars.Face(circle) is Image { Source: Bitmap { PixelSize.Width: > 0 }, IsEffectivelyVisible: true, Bounds: { Width: >= 20, Height: >= 20 } };

    internal static void AvatarCircles()
    {
        GitHubAvatars.Reset();
        byte[] png;
        using (var target = new RenderTargetBitmap(new PixelSize(48, 48)))
        {
            var red = new Border { Width = 48, Height = 48, Background = Brushes.Red };
            red.Measure(new Size(48, 48));
            red.Arrange(new Rect(0, 0, 48, 48));
            target.Render(red);
            using var memory = new MemoryStream();
            target.Save(memory, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            png = memory.ToArray();
        }
        var gate = new TaskCompletionSource();
        var requests = 0;
        var person = new GitHubPerson("user0", AvatarUrl);
        GitHubItem Make(int number) =>
            new(number, "Title " + number, "open", false, "ann", [], 0, "2026-11-01T10:00:00Z", "2026-11-02T10:00:00Z", null, "https://github.com/o/r/issues/" + number, "none", "none") { Assignees = [person] };
        var tab = new GitHubTab(null!, "w", GitHubTab.Issues)
        {
            AvatarSource = async url => { Interlocked.Increment(ref requests); await gate.Task; return png; }
        };
        tab.RenderList(new(false, "r", "o/r", [Make(1), Make(2)], 2, 0, false));
        var window = Show(Scroller(tab));
        var circles = () => window.GetLogicalDescendants().OfType<Border>().Where(b => b.Name == "GitHubAvataruser0").ToList();
        Require(circles() is { Count: 2 } both && both.All(c => GitHubAvatars.Face(c) is TextBlock { Text: "U" }), "Until the image arrives each circle shows the initial");
        var size = circles()[0].Bounds;
        for (var i = 0; i < 200 && Volatile.Read(ref requests) == 0; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Thread.Sleep(50);
        Require(Volatile.Read(ref requests) == 1, "Two rows of one person ask once: " + requests);

        gate.SetResult();
        for (var i = 0; i < 500 && !circles().All(c => GitHubAvatars.Face(c) is Image); i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Frame(window);
        Require(circles().All(c => GitHubAvatars.Face(c) is Image { Source: Bitmap }), "The image replaces the initial in every circle once it arrives");
        Require(circles()[0].Bounds == size, "The circle keeps its size when the image arrives");
        Require(PaintedRed(window, circles()[0]), "The circle is painted with the image");
        window.Close();

        // Another render of the same person, as a refresh or scrolling does, shows the image at once and asks nothing.
        var again = new GitHubTab(null!, "w", GitHubTab.Issues) { AvatarSource = _ => { Interlocked.Increment(ref requests); return Task.FromResult<byte[]?>(png); } };
        again.RenderList(new(false, "r", "o/r", [Make(3), Make(4)], 2, 0, false));
        var second = Show(Scroller(again));
        Require(second.GetLogicalDescendants().OfType<Border>().Where(b => b.Name == "GitHubAvataruser0").All(c => GitHubAvatars.Face(c) is Image), "A second render shows the decoded image at once");
        Require(Volatile.Read(ref requests) == 1, "A second render makes no request: " + requests);
        second.Close();

        // Close the tab and open the issues again: the decoded image is still the cache's own, drawn again, never disposed with a tab.
        foreach (var round in new[] { 1, 2 })
        {
            var reopened = new GitHubTab(null!, "w", GitHubTab.Issues) { AvatarSource = _ => { Interlocked.Increment(ref requests); return Task.FromResult<byte[]?>(png); } };
            reopened.RenderList(new(false, "r", "o/r", [Make(6), Make(7)], 2, 0, false));
            var again2 = Show(Scroller(reopened));
            var circle = again2.GetLogicalDescendants().OfType<Border>().First(b => b.Name == "GitHubAvataruser0");
            Require(GitHubAvatars.Face(circle) is Image { Source: Bitmap shown } && shown.PixelSize.Width > 0, "A reopened tab shows the cached image, round " + round);
            Frame(again2);
            Require(PaintedRed(again2, circle), "A reopened tab paints the cached image, round " + round);
            again2.Close();
        }
        Require(Volatile.Read(ref requests) == 1, "Reopening the tab makes no request: " + requests);

        // A failing host leaves the initial and is not asked again straight away.
        GitHubAvatars.Reset();
        var failing = 0;
        var broken = new GitHubTab(null!, "w", GitHubTab.Issues) { AvatarSource = _ => { Interlocked.Increment(ref failing); throw new IOException("offline"); } };
        broken.RenderList(new(false, "r", "o/r", [Make(5)], 1, 0, false));
        var third = Show(Scroller(broken));
        for (var i = 0; i < 200 && Volatile.Read(ref failing) == 0; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Thread.Sleep(50); Dispatcher.UIThread.RunJobs();
        Require(third.GetLogicalDescendants().OfType<Border>().Any(b => b.Name == "GitHubAvataruser0" && GitHubAvatars.Face(b) is TextBlock { Text: "U" }), "A failed avatar keeps the initial");
        third.Close();
        Console.WriteLine("PASS GitHub avatar circles: initial first, image in place and painted, one request, none on a second render");
        AvatarRing();
    }

    /// <summary>A circle clips its picture to the circle and draws one 1 px theme-border ring on top; the picture scales at high quality.</summary>
    private static void AvatarRing()
    {
        GitHubAvatars.Reset();
        GitHubAvatars.Remember(AvatarUrl, new WriteableBitmap(new PixelSize(8, 8), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888));
        var stack = GitHubAvatars.Stack([new("user0", AvatarUrl), new("user1", "")], null);
        var window = Show(stack);
        foreach (var name in new[] { "GitHubAvataruser0", "GitHubAvataruser1" })
        {
            var circle = (Border)Named(window, name)!;
            Require(circle.ClipToBounds && circle.CornerRadius.TopLeft == circle.Bounds.Width / 2 && circle.Bounds.Width == circle.Bounds.Height, name + " clips to a circle");
            Require(circle.BorderThickness == default, name + " draws its border as a ring over the picture, not as its own edge");
            var ring = circle.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "GitHubAvatarRing");
            Require(ReferenceEquals(ring.BorderBrush, Ui.BorderBrush) && ring.BorderThickness == new Thickness(1) && ring.CornerRadius == circle.CornerRadius && ring.Bounds.Size == circle.Bounds.Size && !ring.IsHitTestVisible, name + " has a 1 px ring in the theme border color over its full circle");
            Require(circle.Child is Grid { Children: [_, var top] } && ReferenceEquals(top, ring), name + " draws the ring last, above the picture");
        }
        Require(GitHubAvatars.Face((Border)Named(window, "GitHubAvataruser0")!) is Image picture && RenderOptions.GetBitmapInterpolationMode(picture) == BitmapInterpolationMode.HighQuality, "The picture scales at high quality");
        window.Close();
        Require(RenderOptions.GetBitmapInterpolationMode(new GitHubAvatar("", 40)) == BitmapInterpolationMode.HighQuality, "The contributor avatar scales at high quality");
        Console.WriteLine("PASS GitHub avatars clip to a circle with one 1 px border ring over the picture");
    }
}