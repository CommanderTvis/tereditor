using System.Globalization;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.CodeIntelligence.UI;

/// <summary>What the page knows about a server's license terms; only a known state may offer Review and accept terms.</summary>
internal enum TermsKnowledge { Checking, NotInstalled, NotAccepted, Accepted, Unavailable }

/// <summary>How a terms state is shown: the status line, whether it is an error, and the review button.</summary>
internal readonly record struct TermsStatusView(string Text, bool Failed, bool ShowReview, bool ReviewEnabled)
{
    internal static TermsStatusView Of(TermsKnowledge knowledge, string acceptedAtUtc = "", string version = "", string detail = "") => knowledge switch
    {
        TermsKnowledge.Accepted => new($"Terms accepted on {acceptedAtUtc}" + (version.Length > 0 ? $" (version {version})." : "."), false, true, false),
        TermsKnowledge.NotAccepted => new("Terms not accepted. Choose Review and accept terms. Nothing starts until you accept.", false, true, true),
        TermsKnowledge.NotInstalled => new("Terms not accepted. Reading them downloads the pinned managed server first.", false, true, true),
        TermsKnowledge.Unavailable => new("Terms unavailable: " + detail, true, true, true),
        _ => new("Checking license terms…", false, false, false),
    };
}

/// <summary>The last known acceptance per server, so reopening the page shows it at once while the host is read again; a new server version drops it.</summary>
internal static class TermsAcceptanceCache
{
    private static readonly Dictionary<string, (string ServerVersion, string AcceptedAtUtc, string Version)> entries = [];

    internal static void Store(string server, string serverVersion, string acceptedAtUtc, string version)
    {
        lock (entries) entries[server] = (serverVersion, acceptedAtUtc, version);
    }

    internal static void Forget(string server)
    {
        lock (entries) entries.Remove(server);
    }

    internal static bool TryGet(string server, string serverVersion, out string acceptedAtUtc, out string version)
    {
        lock (entries)
        {
            if (entries.TryGetValue(server, out var entry))
            {
                if (entry.ServerVersion == serverVersion) { acceptedAtUtc = entry.AcceptedAtUtc; version = entry.Version; return true; }
                entries.Remove(server);
            }
        }
        acceptedAtUtc = version = "";
        return false;
    }
}

/// <summary>The choices of the Java and Kotlin provider.</summary>
internal static class ProviderOptions
{
    internal const string Standard = "standard";

    /// <summary>The offered choices: <c>standard</c> only while every server the replacing ones replace is present, then each replacing server present.</summary>
    internal static string[] Available(IEnumerable<string> presentServers, IEnumerable<(string Id, IReadOnlyList<string> Replaces)> replacing)
    {
        var present = presentServers.ToHashSet();
        var offered = replacing.Where(server => present.Contains(server.Id)).ToArray();
        var replaced = offered.SelectMany(server => server.Replaces).Distinct().ToArray();
        var standard = replaced.Length > 0 && replaced.All(present.Contains);
        return [.. standard ? [Standard] : Array.Empty<string>(), .. offered.Select(server => server.Id)];
    }

    /// <summary>The saved choice when it is offered, else the first offered one (the saved value is never rewritten); empty when none is offered.</summary>
    internal static string Effective(string saved, string[] options) => options.Contains(saved) ? saved : options.FirstOrDefault() ?? "";
}

/// <summary>What the providing plugins say to show for each listed server, so a server named only by id shows the same way.</summary>
internal static class ServerNames
{
    private static readonly Dictionary<string, LanguageServerRow> Rows = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Bitmap> Bitmaps = [];

    internal static void Remember(IEnumerable<LanguageServerRow> servers)
    {
        lock (Rows)
            foreach (var server in servers) Rows[server.Id] = server;
    }

    internal static LanguageServerRow? Row(string id)
    {
        lock (Rows) return Rows.GetValueOrDefault(id);
    }

    internal static string Display(string id) => Row(id) is { Name.Length: > 0 } row ? row.Name : id;

    /// <summary>The mark the server's plugin provides, or a generic one.</summary>
    internal static Control Mark(string server)
    {
        if (Row(server) is not { Icon: { Length: > 0 } data } row) return Ui.Icon("code", Ui.TextBrush);
        if (row.IconFormat == "png")
        {
            if (!Bitmaps.TryGetValue(server, out var bitmap))
            {
                using var input = new MemoryStream(data);
                Bitmaps.Add(server, bitmap = Bitmap.DecodeToWidth(input, 48));
            }
            var frame = new Grid { Width = 24, Height = 24, Tag = "language:" + server };
            frame.Children.Add(new Image { Source = bitmap, Width = 24, Height = 24 });
            return frame;
        }
        return new SvgAsset(data, 24, Ui.TextBrush) { Tag = "language:" + server };
    }
}

/// <summary>Wording the page shares with the Code Intelligence pane.</summary>
internal static class Notes
{
    internal const string Lifetime = "Servers start on demand within the limits below. Agents can start the ones they need and manage this configuration.";
}

/// <summary>Opens the links a server's schema names.</summary>
internal static class ProviderLinks
{
    internal static void Apply(System.Text.Json.Nodes.JsonNode schema, Button website, Button repository, WrapPanel links)
    {
        foreach (var (button, key) in new[] { (website, "website"), (repository, "repository") })
        {
            button.Tag = Uri.TryCreate(schema[key]?.ToString(), UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri : null;
            button.IsVisible = button.Tag is Uri;
            ToolTip.SetTip(button, button.Tag?.ToString());
        }
        if (website.Tag is Uri site && repository.Tag is Uri source &&
            site.AbsoluteUri.TrimEnd('/').Equals(source.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal))
            website.IsVisible = false;
        repository.Content = (schema["repositoryLabel"]?.ToString() ?? "Source repository") + " ↗";
        links.IsVisible = website.IsVisible || repository.IsVisible;
    }

    /// <summary>Opens a link through the window's launcher.</summary>
    internal static Func<TopLevel, Uri, Task> Launch { get; set; } = (top, uri) => top.Launcher.LaunchUriAsync(uri);

    /// <summary>Opens a link activated inside a <see cref="LinkedTextBlock"/> through the same launcher.</summary>
    internal static async void OnLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control control && TopLevel.GetTopLevel(control) is { } top) await Launch(top, e.Url);
    }

    internal static async void Open(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri uri } button && TopLevel.GetTopLevel(button) is { } top)
            await Launch(top, uri);
    }
}