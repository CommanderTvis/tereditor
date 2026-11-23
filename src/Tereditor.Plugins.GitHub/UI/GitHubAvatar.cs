using System.Net.Http;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// Avatar pictures, decoded small and kept in a least recently used cache of about 16 MB. They load off the UI thread over http and
/// https only, send no credentials and never throw: a picture that cannot load stays a neutral circle.
/// </summary>
internal static class GitHubAvatarCache
{
    internal const long Budget = 16L << 20;
    private const int Decoded = 80;

    private sealed record Entry(string Url, Bitmap Picture, long Bytes);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Map = [];
    private static readonly LinkedList<Entry> Order = new();
    private static readonly HashSet<string> Failed = [];
    private static readonly SemaphoreSlim Slots = new(4);
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) };
    private static long used;

    /// <summary>Bytes the cache holds now.</summary>
    internal static long Used { get { lock (Gate) return used; } }

    internal static bool Allowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.UserInfo.Length == 0;

    public static Bitmap? TryGet(string url)
    {
        lock (Gate)
        {
            if (!Map.TryGetValue(url, out var node)) return null;
            Order.Remove(node); Order.AddFirst(node);
            return node.Value.Picture;
        }
    }

    /// <summary>The picture, from the cache or the network; null when the address is not allowed or the load failed.</summary>
    public static async Task<Bitmap?> LoadAsync(string url, CancellationToken ct)
    {
        if (!Allowed(url)) return null;
        if (TryGet(url) is { } hit) return hit;
        lock (Gate) if (Failed.Contains(url)) return null;
        try
        {
            await Slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                var picture = Bitmap.DecodeToWidth(stream, Decoded);
                Add(url, picture, (long)picture.PixelSize.Width * picture.PixelSize.Height * 4);
                return picture;
            }
            finally { Slots.Release(); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            lock (Gate) Failed.Add(url);
            return null;
        }
    }

    internal static void Add(string url, Bitmap picture, long bytes)
    {
        lock (Gate)
        {
            if (Map.Remove(url, out var old)) { Order.Remove(old); used -= old.Value.Bytes; }
            Map[url] = Order.AddFirst(new Entry(url, picture, bytes));
            used += bytes;
            while (used > Budget && Order.Last is { } last && Order.Count > 1)
            {
                Order.RemoveLast(); Map.Remove(last.Value.Url); used -= last.Value.Bytes;
            }
        }
    }
}

/// <summary>A round avatar: a neutral circle until the picture is in, and for good when it cannot load.</summary>
internal sealed class GitHubAvatar : Control
{
    private readonly string url;
    private Bitmap? picture;
    private CancellationTokenSource? lifetime;

    public GitHubAvatar(string url, double size)
    {
        this.url = url; Width = size; Height = size;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        picture = url.Length > 0 ? GitHubAvatarCache.TryGet(url) : null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (picture is not null || !GitHubAvatarCache.Allowed(url)) return;
        lifetime = new();
        var token = lifetime.Token;
        _ = Task.Run(async () =>
        {
            var loaded = await GitHubAvatarCache.LoadAsync(url, token).ConfigureAwait(false);
            if (loaded is not null) Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) { picture = loaded; InvalidateVisual(); } });
        }, token);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        lifetime?.Cancel(); lifetime = null;
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (picture is null) context.DrawEllipse(Ui.Hover, null, rect.Center, rect.Width / 2, rect.Height / 2);
        else
            using (context.PushGeometryClip(new EllipseGeometry(rect)))
                context.DrawImage(picture, new Rect(picture.Size), rect);
        // The 1 px border sits inside the circle and is drawn over the picture, so the edge is one antialiased line.
        context.DrawEllipse(null, new Pen(Ui.BorderBrush, 1), rect.Center, (rect.Width - 1) / 2, (rect.Height - 1) / 2);
    }
}