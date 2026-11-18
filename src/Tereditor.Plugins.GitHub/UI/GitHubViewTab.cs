using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// The tabs built from generic views: Actions, Discussions, Releases, Contributors, Deployments and Packages. Ids: the kind
/// (<c>runs</c>, <c>discussions</c>, ...) for a list, and <c>run:ID</c> or <c>discussion:N</c> for one item. Like the issue tabs it
/// polls while shown and redraws only when the host's revision moves.
/// </summary>
internal sealed class GitHubViewTab : Border
{
    private readonly IPluginUIContext context;
    private readonly string workspace;
    private readonly bool standalone;
    private readonly string kind;
    private readonly GitHubPage page = new();
    private ScrollViewer scroll => page.Scroll;
    private readonly DispatcherTimer poll = new();
    private readonly Dictionary<string, string> filters = [];
    private readonly GitHubLinker linker;
    private readonly List<GitHubCard> pendingCards = [];
    private GitHubComposer? composer;
    private GitHubView? shown;
    private string repoSlug = "";
    private string itemId = "";
    private int limit = 30;
    private string? revision;
    private int generation;
    private CancellationTokenSource? lifetime;
    private readonly GitHubFreshness freshness;

    public GitHubViewTab(IPluginUIContext context, string workspace, string id)
    {
        this.context = context;
        this.workspace = workspace;
        Name = "GitHubViewTab";
        var parts = id.Split(':', 2);
        kind = parts[0] switch { "run" => "runs", "discussion" => "discussions", var other => other };
        if (parts.Length == 2) { itemId = parts[1]; standalone = true; }
        linker = new(() => repoSlug, HandleLink);
        freshness = new(page, Refresh);
        Child = freshness;
        page.Show(null, Ui.Status("Loading…"));
        poll.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { lifetime = new(); poll.Interval = TimeSpan.FromSeconds(Math.Clamp(context.Settings<GitHubSettings>().PollSeconds, 15, 600)); poll.Start(); revision = null; Refresh(); };
        DetachedFromVisualTree += (_, _) => { poll.Stop(); lifetime?.Cancel(); generation++; };
    }

    private bool HandleLink(string url)
    {
        if (GitHubLinks.Parse(url, repoSlug) is not { } target) return false;
        var parts = target.Id.Split(':', 2);
        if (!standalone && itemId.Length == 0 && parts.Length == 2 && (parts[0] == "run" && kind == "runs" || parts[0] == "discussion" && kind == "discussions"))
        {
            itemId = parts[1]; revision = null; Refresh();
        }
        else _ = OpenLinkedAsync(target.Id, target.Title);
        return true;
    }

    private async Task OpenLinkedAsync(string id, string title)
    {
        try { await context.OpenDocumentAsync(workspace, GitHubContract.Scheme, id, title); }
        catch (Exception error) when (error is not OperationCanceledException) { context.Notify(PluginNotificationKind.Error, "Couldn't open GitHub", error.Message); }
    }

    private void Refresh()
    {
        var mine = ++generation;
        var token = lifetime?.Token ?? CancellationToken.None;
        _ = LoadAsync(mine, token);
    }

    private async Task LoadAsync(int mine, CancellationToken token)
    {
        var request = new GitHubViewParams(workspace, kind, itemId, [.. filters.Select(f => new GitHubFilterValue(f.Key, f.Value))], limit, revision);
        GitHubView view;
        try { view = await Task.Run(async () => await context.RequestAsync(GitHubContract.View, request, token).AsTask(), token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { if (mine == generation) page.Show(null, Message(error.Message)); return; }
        if (mine != generation || token.IsCancellationRequested) return;
        freshness.Apply(view.Cached);
        if (view.Problem is not null) { page.Show(null, Message(view.Problem)); return; }
        if (view.Unchanged) return;
        revision = view.Revision;
        shown = view;
        repoSlug = RepoFrom(view);
        Render(view);
    }

    private static string RepoFrom(GitHubView view)
    {
        var url = view.Rows.Select(r => r.Url).Concat([view.Summary]).FirstOrDefault(u => u.StartsWith("https://github.com/", StringComparison.Ordinal)) ?? "";
        var parts = url.Split('/');
        return parts.Length > 4 ? parts[3] + "/" + parts[4] : "";
    }

    internal void Render(GitHubView view)
    {
        var offset = scroll.Offset;
        var body = Build(view, out var top);
        page.Show(top, body);
        Dispatcher.UIThread.Post(() => scroll.Offset = offset, DispatcherPriority.Loaded);
    }

    private void Posted(GitHubEvent? posted, string? state)
    {
        if (posted is not null) pendingCards.Add(new("", posted.Author, posted.At, "", posted.Body) { Id = posted.Id });
        if (shown is not null) Render(shown);
        revision = null;
        Refresh();
    }

    private Control Build(GitHubView view, out Control top)
    {
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(24, 0, 24, 16), MaxWidth = 1012, Name = "GitHubView" };
        var header = new StackPanel { Spacing = 12, Margin = new Thickness(24, 16, 24, 12), MaxWidth = 1012 };
        top = header;
        if (itemId.Length > 0 && !standalone)
            header.Children.Add(Ui.Button(kind == "runs" ? "Actions" : "Discussions", () => { itemId = ""; revision = null; Refresh(); }, GitHubIcons.ArrowLeft));
        var title = Ui.Text(view.Title, Ui.TextBrush, 22);
        title.FontWeight = FontWeight.SemiBold; title.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(title);
        if (view.Subtitle.Length > 0)
        {
            var sub = Ui.Text(view.Subtitle, Ui.Muted, 13); sub.TextWrapping = TextWrapping.Wrap; sub.TextTrimming = TextTrimming.None;
            header.Children.Add(sub);
        }
        if (view.State is not "none" && itemId.Length > 0) header.Children.Add(Ui.Text(StateText(view.State), GitHubTab.Muted(StateTone(view.State)), 14));
        if (view.Filters.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var filter in view.Filters) chips.Children.Add(Chip(filter));
            header.Children.Add(chips);
        }
        if (view.Summary.Length > 0 && itemId.Length == 0) header.Children.Add(Ui.Text(view.Summary + (kind == "runs" ? "   Auto-refresh: " + (int)poll.Interval.TotalSeconds + "s" : ""), Ui.Muted, 13));
        if (view.Rows.Count > 0 || (view.Cards.Count == 0 && itemId.Length == 0))
        {
            var frame = new StackPanel { Name = "GitHubRows" };
            foreach (var row in view.Rows) frame.Children.Add(Row(row));
            if (view.Rows.Count == 0) frame.Children.Add(new Border { Padding = new Thickness(24), Child = Ui.Text("No results matched your search.") });
            root.Children.Add(new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = frame });
        }
        var remaining = GitHubPending.Remaining(pendingCards, view.Cards);
        pendingCards.Clear(); pendingCards.AddRange(remaining);
        foreach (var card in view.Cards.Concat(pendingCards)) root.Children.Add(Card(card));
        if (kind == "discussions" && itemId.Length > 0 && view.Cards.Count > 0)
        {
            var number = itemId;
            if (composer is null || composerKey != number)
            {
                composer = new GitHubComposer($"{workspace}|discussions|{number}", false, async (action, body) =>
                    await Task.Run(async () => await context.RequestAsync(GitHubContract.Post, new GitHubPostParams(workspace, "discussions", int.Parse(number), action, body), lifetime?.Token ?? default).AsTask()),
                    Posted, linker);
                composerKey = number;
            }
            composer.Detach();
            root.Children.Add(composer);
        }
        if (view.HasMore)
        {
            var more = Ui.Button("Show more", () => { limit += 30; revision = null; Refresh(); });
            more.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(more);
        }
        return root;
    }

    private string composerKey = "";

    private Control Chip(GitHubFilter filter)
    {
        var chipText = Ui.Text(filter.Label + (filter.Value.Length > 0 ? ": " + filter.Value : "") + " ▾", Ui.TextBrush, 13);
        var button = new Button { Name = "GitHubChip" + filter.Label, Content = chipText, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 8, 0) };
        HoverLink.Apply(chipText, button);
        var flyout = new MenuFlyout();
        flyout.Items.Add(Ui.Menu("Clear " + filter.Label.ToLowerInvariant(), () => Choose(filter.Key, "")));
        foreach (var option in filter.Options) flyout.Items.Add(Ui.Menu(option, () => Choose(filter.Key, option)));
        button.Flyout = flyout;
        return button;
    }

    private void Choose(string key, string value)
    {
        if (value.Length == 0) filters.Remove(key); else filters[key] = value;
        limit = 30; revision = null; Refresh();
    }

    private Control Row(GitHubRow row)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(12 + row.Indent * 28, 8, 12, 8) };
        var icon = Ui.Icon(StateGlyph(row.State), GitHubTab.Muted(StateTone(row.State)), 16);
        icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 2, 0, 0);
        if (row.State != "none") grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(row.State == "none" ? 0 : 10, 0, 0, 0) };
        var titleRow = new WrapPanel();
        var title = Ui.Text(row.Title, Ui.TextBrush, 14);
        title.Name = "GitHubTitle" + row.Id;
        title.FontWeight = row.Indent == 0 ? FontWeight.SemiBold : FontWeight.Normal;
        title.Margin = new Thickness(0, 0, 8, 0);
        titleRow.Children.Add(title);
        foreach (var chip in row.Chips) titleRow.Children.Add(GitHubTab.LabelChip(chip.Color.Length == 0 ? chip with { Color = "ddf4ff" } : chip));
        text.Children.Add(titleRow);
        if (row.Meta.Length > 0 || row.At.Length > 0)
            text.Children.Add(Ui.Text(row.Meta + (row.At.Length > 0 ? (row.Meta.Length > 0 ? " · " : "") + GitHubTab.Ago(row.At) : ""), Ui.Muted, 12));
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (row.Right.Length > 0) { var right = Ui.Text(row.Right, Ui.Muted, 12); Grid.SetColumn(right, 2); grid.Children.Add(right); }
        var border = new Border { Name = "GitHubRow" + row.Id, Child = grid, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1) };
        if (itemId.Length == 0 && kind is "runs" or "discussions")
        {
            border.Background = Brushes.Transparent;
            HoverLink.Apply(title, border, () => { itemId = row.Id; revision = null; Refresh(); });
            border.PointerPressed += (_, e) =>
            {
                var point = e.GetCurrentPoint(border).Properties;
                if (point.IsRightButtonPressed || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta) || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) _ = OpenTabAsync(row);
                else if (point.IsLeftButtonPressed) { itemId = row.Id; revision = null; Refresh(); }
            };
        }
        return border;
    }

    private async Task OpenTabAsync(GitHubRow row)
    {
        var run = kind == "runs";
        try { await context.OpenDocumentAsync(workspace, GitHubContract.Scheme, (run ? "run:" : "discussion:") + row.Id, (run ? "Run " : "Discussion #") + (run ? row.Title : row.Id)); }
        catch (Exception error) when (error is not OperationCanceledException) { context.Notify(PluginNotificationKind.Error, "Couldn't open GitHub", error.Message); }
    }

    private Control Card(GitHubCard card)
    {
        var header = card.Title.Length > 0 ? card.Title : "";
        var body = card.Body.Length == 0 ? "No description provided." : card.Body;
        var shell = GitHubTab.Card(card.Author, card.At, (card.Note + (card.Badge.Length > 0 ? " · " + card.Badge : "")).Trim(), body, null, linker);
        if (header.Length == 0) return shell;
        var panel = new StackPanel { Spacing = 6 };
        var heading = Ui.Text(header + (card.Badge.Length > 0 ? "  [" + card.Badge + "]" : ""), Ui.TextBrush, 18);
        heading.FontWeight = FontWeight.SemiBold;
        panel.Children.Add(heading);
        panel.Children.Add(shell);
        return panel;
    }

    private static string StateTone(string state) => state switch { "success" or "open" or "answered" => "passing", "failure" => "failing", _ => "pending" };

    private static string StateText(string state) => state switch
    {
        "success" => "Success",
        "failure" => "Failed",
        "cancelled" => "Cancelled",
        "skipped" => "Skipped",
        "running" => "In progress",
        "queued" => "Queued",
        "answered" => "Answered",
        "closed" => "Closed",
        _ => "Open"
    };

    private static string StateGlyph(string state) => state switch
    {
        "success" or "answered" => GitHubIcons.CheckboxCircleFill,
        "failure" => GitHubIcons.CloseCircleFill,
        "cancelled" => GitHubIcons.StopCircle,
        "skipped" => GitHubIcons.SkipForward,
        "running" => GitHubIcons.Loader,
        "queued" => GitHubIcons.Time,
        "closed" => GitHubIcons.CheckboxCircle,
        _ => GitHubIcons.RecordCircle
    };

    private static Control Message(string text)
    {
        var line = Ui.Status(text, Ui.Muted, 14);
        line.Name = "GitHubMessage";
        return line;
    }
}