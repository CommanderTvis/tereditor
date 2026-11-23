using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// Overlapping round avatars of the people an item is assigned to: up to three, then a +N count. Each shows the login's initial until its
/// image arrives and keeps it when the image fails; the image replaces the initial in the same circle, so nothing moves. The host fetches and
/// saves the bytes (the UI never reaches the network); the UI decodes each address once, off the UI thread, and keeps up to
/// <see cref="Kept"/> decoded images, so scrolling, refreshing and every row of one person never ask again. A failed address is not asked
/// again for a minute. The login is the tooltip.
/// </summary>
internal static class GitHubAvatars
{
    internal const int Shown = 3, Kept = 128;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private static readonly Dictionary<string, Bitmap> Decoded = [];
    private static readonly Queue<string> Order = new();
    private static readonly Dictionary<string, Task<Bitmap?>> Flights = [];
    private static readonly Dictionary<string, long> Failed = [];

    /// <summary>Puts an image in the decoded cache as if it had been fetched, so checks need no host. The cache owns it for the life of the process.</summary>
    internal static void Remember(string url, Bitmap bitmap)
    {
        if (GitHubAvatarUrl.Normalize(url) is not { } uri) return;
        lock (Decoded) { Decoded[uri.AbsoluteUri] = bitmap; Order.Enqueue(uri.AbsoluteUri); }
    }

    /// <summary>Forgets every decoded image (for checks).</summary>
    internal static void Reset()
    {
        lock (Decoded) { Decoded.Clear(); Order.Clear(); Flights.Clear(); Failed.Clear(); }
    }

    internal static Control Stack(IReadOnlyList<GitHubPerson> people, Func<string, Task<byte[]?>>? fetch)
    {
        var stack = new StackPanel { Name = "GitHubAssignees", Orientation = Orientation.Horizontal, Spacing = -4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var person in people.Take(Shown)) stack.Children.Add(Avatar(person, fetch));
        if (people.Count > Shown)
        {
            var more = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.Elevated, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), Child = Ui.Text("+" + (people.Count - Shown), Ui.Muted, 12) };
            ((TextBlock)more.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(more, string.Join(", ", people.Skip(Shown).Select(p => p.Login)));
            stack.Children.Add(more);
        }
        return stack;
    }

    private static Border Avatar(GitHubPerson person, Func<string, Task<byte[]?>>? fetch)
    {
        var initial = Ui.Text(person.Login.Length > 0 ? person.Login[..1].ToUpperInvariant() : "·", Ui.TextBrush, 12);
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        var face = new Grid { Children = { initial, Ring() } };
        var avatar = new Border { Name = "GitHubAvatar" + person.Login, Width = 24, Height = 24, CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = Ui.Elevated, Child = face };
        ToolTip.SetTip(avatar, person.Login);
        if (GitHubAvatarUrl.Normalize(person.AvatarUrl) is not { } uri) return avatar;
        var key = uri.AbsoluteUri;
        Bitmap? ready;
        lock (Decoded) Decoded.TryGetValue(key, out ready);
        if (ready is not null) Face(avatar, Picture(ready));
        else if (fetch is not null) _ = ShowAsync(avatar, key, person.AvatarUrl, fetch);
        return avatar;
    }

    private static Image Picture(Bitmap bitmap)
    {
        var picture = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapInterpolationMode(picture, BitmapInterpolationMode.HighQuality);
        return picture;
    }

    /// <summary>The 1 px border, drawn over the picture so the circle's edge is one clean line and not the picture's rim.</summary>
    internal static Border Ring() => new() { Name = "GitHubAvatarRing", CornerRadius = new CornerRadius(12), BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), IsHitTestVisible = false };

    /// <summary>What a circle shows under its ring: the initial, then the picture.</summary>
    internal static Control? Face(Border avatar) => (avatar.Child as Grid)?.Children[0];

    private static void Face(Border avatar, Control content) => ((Grid)avatar.Child!).Children[0] = content;

    private static async Task ShowAsync(Border avatar, string key, string url, Func<string, Task<byte[]?>> fetch)
    {
        var bitmap = await Flight(key, url, fetch);
        if (bitmap is not null) await Dispatcher.UIThread.InvokeAsync(() => Face(avatar, Picture(bitmap)));
    }

    private static Task<Bitmap?> Flight(string key, string url, Func<string, Task<byte[]?>> fetch)
    {
        lock (Decoded)
        {
            if (Decoded.TryGetValue(key, out var done)) return Task.FromResult<Bitmap?>(done);
            if (Flights.TryGetValue(key, out var running)) return running;
            if (Failed.TryGetValue(key, out var at) && Environment.TickCount64 - at < RetryAfter.TotalMilliseconds) return Task.FromResult<Bitmap?>(null);
            return Flights[key] = Task.Run(() => LoadAsync(key, url, fetch));
        }
    }

    private static async Task<Bitmap?> LoadAsync(string key, string url, Func<string, Task<byte[]?>> fetch)
    {
        Bitmap? bitmap = null;
        try
        {
            if (await fetch(url) is { } bytes)
            {
                using var memory = new MemoryStream(bytes);
                bitmap = new Bitmap(memory);
            }
        }
        // Whatever goes wrong (host error, undecodable bytes) leaves the initial in the circle.
        catch (Exception error) when (error is not OutOfMemoryException) { }
        lock (Decoded)
        {
            Flights.Remove(key);
            if (bitmap is null) Failed[key] = Environment.TickCount64;
            else
            {
                Decoded[key] = bitmap;
                Order.Enqueue(key);
                while (Order.Count > Kept) Decoded.Remove(Order.Dequeue());
            }
        }
        return bitmap;
    }
}