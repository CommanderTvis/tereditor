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
/// image arrives and keeps it when the image fails. Images load like the Markdown preview's: http and https only, 1 MB at most, no
/// credentials, off the UI thread. The login is the tooltip.
/// </summary>
internal static class GitHubAvatars
{
    internal const int Shown = 3, MaxBytes = 1024 * 1024;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, Bitmap> Cache = [];

    internal static Control Stack(IReadOnlyList<GitHubPerson> people, bool load = true)
    {
        var stack = new StackPanel { Name = "GitHubAssignees", Orientation = Orientation.Horizontal, Spacing = -4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var person in people.Take(Shown)) stack.Children.Add(Avatar(person, load));
        if (people.Count > Shown)
        {
            var more = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.Elevated, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), Child = Ui.Text("+" + (people.Count - Shown), Ui.Muted, 12) };
            ((TextBlock)more.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(more, string.Join(", ", people.Skip(Shown).Select(p => p.Login)));
            stack.Children.Add(more);
        }
        return stack;
    }

    private static Border Avatar(GitHubPerson person, bool load)
    {
        var initial = Ui.Text(person.Login.Length > 0 ? person.Login[..1].ToUpperInvariant() : "·", Ui.TextBrush, 12);
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        var avatar = new Border { Name = "GitHubAvatar" + person.Login, Width = 24, Height = 24, CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = Ui.Elevated, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), Child = initial };
        ToolTip.SetTip(avatar, person.Login);
        if (load && Uri.TryCreate(person.AvatarUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") _ = LoadAsync(avatar, uri);
        return avatar;
    }

    private static async Task LoadAsync(Border avatar, Uri uri)
    {
        try
        {
            Bitmap? bitmap;
            lock (Cache) Cache.TryGetValue(uri.AbsoluteUri, out bitmap);
            if (bitmap is null)
            {
                bitmap = await Task.Run(async () =>
                {
                    using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException("Image too large.");
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var memory = new MemoryStream();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(buffer)) > 0)
                    {
                        if (memory.Length + count > MaxBytes) throw new IOException("Image too large.");
                        memory.Write(buffer, 0, count);
                    }
                    memory.Position = 0;
                    return new Bitmap(memory);
                });
                lock (Cache) { if (Cache.Count >= 64) Cache.Clear(); Cache[uri.AbsoluteUri] = bitmap; }
            }
            await Dispatcher.UIThread.InvokeAsync(() => avatar.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill });
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException or NotSupportedException) { }
    }
}