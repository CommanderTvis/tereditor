using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// The Actions tab, laid out like GitHub's: a workflow sidebar with its own scroll, then a pinned header (title, subtitle, search) and
/// table header (count, auto-refresh, filters) above a virtualized run list. The list holds light <see cref="GitHubRun"/> records
/// only; a row exists as controls while it is in view. The host pages the data: the next page loads when the list scrolls near its end.
/// A poll reads page one and changes only the rows whose run changed.
/// </summary>
internal sealed class GitHubActionsTab : Border
{
    internal const int PageSize = 30;
    internal const int MaxRuns = 500;
    private static readonly string[] Qualifiers = ["branch", "event", "actor", "status", "created", "head_sha"];

    private sealed record Footer(string Text);

    private readonly IPluginUIContext? context;
    private readonly string workspace;
    private readonly Func<GitHubViewParams, CancellationToken, Task<GitHubView>> loadView;
    private readonly Func<DateTimeOffset> now;
    private readonly TimeZoneInfo zone;
    private readonly ObservableCollection<object> items = [];
    private readonly Dictionary<string, string> filters = [];
    private readonly DispatcherTimer poll = new();
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Name = "GitHubRunScroll" };
    private readonly StackPanel sidebarItems = new() { Name = "GitHubWorkflows", Margin = new Thickness(8) };
    private readonly Border sidebar = new() { Name = "GitHubSidebar", Width = 240, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 1, 0) };
    private readonly Border collapsed = new() { IsVisible = false, Width = 40, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 1, 0) };
    private readonly TextBlock title = Ui.Text("All workflows", Ui.TextBrush, 24);
    private readonly TextBlock subtitle = Ui.Text("Showing runs from all workflows", Ui.Muted, 13);
    private readonly TextBlock count = Ui.Text("", Ui.TextBrush, 13);
    private readonly Button refreshButton = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Name = "GitHubAutoRefresh" };
    private readonly StackPanel chips = new() { Orientation = Orientation.Horizontal, Name = "GitHubRunFilters" };
    private readonly TextBox search = new() { Name = "GitHubRunSearch", PlaceholderText = "Search runs" };
    private readonly TextBlock centre = Ui.Text("Loading…", Ui.Muted, 14);
    private readonly ScrollViewer sidebarScroll;
    private IReadOnlyList<GitHubWorkflow> workflows = [];
    private string repoUrl = "";
    private int total;
    private int pages;
    private bool hasMore;
    private bool busy;
    private bool managementOpen;
    private int refreshSeconds = 30;
    private string? revision;
    private int generation;
    private CancellationTokenSource? lifetime;
    private readonly GitHubFreshness freshness;

    public GitHubActionsTab(IPluginUIContext? context, string workspace, Func<GitHubViewParams, CancellationToken, Task<GitHubView>>? loadView = null,
        Func<DateTimeOffset>? now = null, TimeZoneInfo? zone = null)
    {
        this.context = context;
        this.workspace = workspace;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.zone = zone ?? TimeZoneInfo.Local;
        this.loadView = loadView ?? ((request, token) => context is null ? Task.FromException<GitHubView>(new InvalidOperationException("No host.")) : context.RequestAsync(GitHubContract.View, request, token).AsTask());
        Name = "GitHubActionsTab";
        title.FontWeight = FontWeight.SemiBold; title.TextWrapping = TextWrapping.Wrap;
        centre.HorizontalAlignment = HorizontalAlignment.Center; centre.VerticalAlignment = VerticalAlignment.Center; centre.Name = "GitHubRunsCentre";
        sidebarScroll = new ScrollViewer { Content = sidebarItems, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Name = "GitHubSidebarScroll" };
        BuildSidebar();

        var newWorkflow = Ui.Button("New workflow", () => GitHubRuns.Open(repoUrl + "/actions/new"));
        var titleRow = new Grid { ColumnDefinitions = new("*,Auto") };
        titleRow.Children.Add(title); Grid.SetColumn(newWorkflow, 1); titleRow.Children.Add(newWorkflow);
        search.InnerRightContent = Ui.IconButton(GitHubIcons.Search, "Search runs", ApplySearch);
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySearch(); };
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(24, 16, 24, 12), Children = { titleRow, subtitle, search } };

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        left.Children.Add(count);
        left.Children.Add(refreshButton);
        var tableHead = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(12, 8), Background = Ui.Header, Name = "GitHubRunTableHeader" };
        tableHead.Children.Add(left); Grid.SetColumn(chips, 1); tableHead.Children.Add(chips);
        var headBorder = new Border { Background = Ui.Header, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = tableHead };

        var list = new ItemsControl
        {
            Name = "GitHubRuns",
            ItemsSource = items,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch { GitHubRun run => RunRow(run), Footer footer => FooterRow(footer), _ => null })
        };
        scroll.Content = list;
        scroll.ScrollChanged += (_, _) => MaybeMore();
        var listArea = new Grid { Children = { scroll, centre } };
        var table = new Grid { RowDefinitions = new("Auto,*") };
        table.Children.Add(headBorder); Grid.SetRow(listArea, 1); table.Children.Add(listArea);
        var frame = new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(24, 0, 24, 16), Child = table };

        var main = new Grid { RowDefinitions = new("Auto,*"), Name = "GitHubActionsMain" };
        main.Children.Add(header); Grid.SetRow(frame, 1); main.Children.Add(frame);
        var root = new Grid { ColumnDefinitions = new("Auto,*") };
        var side = new Grid { Children = { sidebar, collapsed } };
        root.Children.Add(side); Grid.SetColumn(main, 1); root.Children.Add(main);
        freshness = new(root, () => { if (!busy) _ = PollAsync(); });
        Child = freshness;
        UpdateRefreshButton();

        poll.Tick += (_, _) => { if (!busy) _ = PollAsync(); };
        AttachedToVisualTree += (_, _) => { lifetime = new(); StartPolling(); revision = null; _ = ReloadAsync(); };
        DetachedFromVisualTree += (_, _) => { poll.Stop(); lifetime?.Cancel(); generation++; };
    }

    internal ObservableCollection<object> Items => items;

    internal int RunCount => items.Count(item => item is GitHubRun);

    internal ScrollViewer Scroller => scroll;

    internal Action<GitHubRun>? RunOpened { get; set; }

    internal string Filter(string key) => filters.GetValueOrDefault(key, "");

    private void StartPolling()
    {
        poll.Stop();
        if (refreshSeconds <= 0) return;
        poll.Interval = TimeSpan.FromSeconds(refreshSeconds);
        poll.Start();
    }

    internal void SetAutoRefresh(int seconds) { refreshSeconds = seconds; UpdateRefreshButton(); if (lifetime is not null) StartPolling(); }

    internal bool Polling => poll.IsEnabled;

    private void UpdateRefreshButton()
    {
        refreshButton.Content = Ui.Text("Auto-refresh: " + (refreshSeconds > 0 ? refreshSeconds + "s" : "off") + " ▾", Ui.TextBrush, 13);
        var flyout = new MenuFlyout();
        flyout.Items.Add(Ui.Menu("30s", () => SetAutoRefresh(30)));
        flyout.Items.Add(Ui.Menu("60s", () => SetAutoRefresh(60)));
        flyout.Items.Add(Ui.Menu("Off", () => SetAutoRefresh(0)));
        refreshButton.Flyout = flyout;
    }

    // Loading

    private GitHubViewParams Request(int page, string? known) =>
        new(workspace, "runs", "", [.. filters.Select(f => new GitHubFilterValue(f.Key, f.Value))], PageSize, known) { Page = page };

    /// <summary>Starts again from page one: after a filter, a workflow or a search changes.</summary>
    internal async Task ReloadAsync()
    {
        var mine = ++generation;
        busy = true;
        items.Clear(); pages = 0; hasMore = false; revision = null;
        scroll.Offset = default;
        centre.Text = "Loading…"; centre.IsVisible = true;
        var view = await FetchAsync(1, null, mine);
        if (view is null) return;
        busy = false;
        freshness.Apply(view.Cached);
        if (view.Problem is not null) { Problem(view.Problem); return; }
        Header(view);
        pages = 1;
        revision = view.Revision;
        foreach (var run in view.Runs) items.Add(run);
        hasMore = view.HasMore;
        SetFooter();
        centre.IsVisible = items.Count == 0;
        centre.Text = "No workflow runs matched your search.";
        Dispatcher.UIThread.Post(MaybeMore, DispatcherPriority.Loaded);
    }

    private async Task<GitHubView?> FetchAsync(int page, string? known, int mine)
    {
        var token = lifetime?.Token ?? CancellationToken.None;
        GitHubView view;
        try { view = await Task.Run(() => loadView(Request(page, known), token), token); }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) { view = new(false, "", "", "", "none", [], [], [], false, "", error.Message); }
        if (mine != generation || token.IsCancellationRequested) return null;
        return view;
    }

    private void Problem(string text)
    {
        items.Clear();
        centre.Text = text; centre.IsVisible = true;
    }

    internal async Task PollAsync()
    {
        var mine = generation;
        busy = true;
        var view = await FetchAsync(1, revision, mine);
        busy = false;
        if (view is not null) freshness.Apply(view.Cached);
        if (view is null || view.Problem is not null || view.Unchanged) return;
        revision = view.Revision;
        Header(view);
        MergeHead(view.Runs);
        hasMore = pages * PageSize < total;
        SetFooter();
    }

    internal async Task MoreAsync()
    {
        if (busy || !hasMore || RunCount >= MaxRuns) return;
        var mine = generation;
        busy = true;
        SetFooter(true);
        var view = await FetchAsync(pages + 1, null, mine);
        busy = false;
        if (view is null) return;
        if (view.Problem is not null) { hasMore = false; SetFooter(); return; }
        pages++;
        var seen = items.OfType<GitHubRun>().Select(run => run.Id).ToHashSet();
        var at = items.Count > 0 && items[^1] is Footer ? items.Count - 1 : items.Count;
        foreach (var run in view.Runs.Where(run => seen.Add(run.Id)).Take(MaxRuns - RunCount)) items.Insert(at++, run);
        hasMore = view.HasMore;
        SetFooter();
        Dispatcher.UIThread.Post(MaybeMore, DispatcherPriority.Loaded);
    }

    private void MaybeMore()
    {
        if (busy || !hasMore || RunCount >= MaxRuns || items.Count == 0) return;
        var left = scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height;
        if (scroll.Viewport.Height > 0 && scroll.Extent.Height > 0 && left < 400) _ = MoreAsync();
    }

    private void SetFooter(bool loading = false)
    {
        var had = items.Count > 0 && items[^1] is Footer;
        var capped = RunCount >= MaxRuns && total > RunCount;
        Footer? want = items.Count == 0 ? null : loading ? new("Loading more…") : capped ? new("Showing the latest " + MaxRuns + " runs. Refine the search to see others.") : hasMore ? new("") : null;
        if (had && want is not null) { if (!Equals(items[^1], want)) items[^1] = want; return; }
        if (had) items.RemoveAt(items.Count - 1);
        else if (want is not null) items.Add(want);
    }

    private void Header(GitHubView view)
    {
        total = view.Total;
        repoUrl = view.RepoUrl.Length > 0 ? view.RepoUrl : repoUrl;
        freshness.Url = repoUrl.Length > 0 ? repoUrl + "/actions" : "";
        title.Text = view.Title;
        subtitle.Text = view.Subtitle;
        count.Text = view.Summary;
        if (view.Workflows.Count > 0 && !view.Workflows.SequenceEqual(workflows)) { workflows = view.Workflows; BuildSidebar(); }
        chips.Children.Clear();
        foreach (var filter in view.Filters) chips.Children.Add(Dropdown(filter));
    }

    /// <summary>Applies a poll's page one: a run that did not change keeps its row, a changed one is replaced, a new one is inserted.</summary>
    internal void MergeHead(IReadOnlyList<GitHubRun> fresh)
    {
        var k = 0;
        foreach (var run in fresh)
        {
            if (k < items.Count && items[k] is GitHubRun at && at.Id == run.Id) { if (at != run) items[k] = run; k++; continue; }
            var later = -1;
            for (var i = k + 1; i < items.Count && later < 0; i++)
                if (items[i] is GitHubRun candidate && candidate.Id == run.Id) later = i;
            if (later > 0)
            {
                for (var drop = later - 1; drop >= k; drop--) items.RemoveAt(drop);
                if (items[k] is GitHubRun same && same != run) items[k] = run;
            }
            else items.Insert(k, run);
            k++;
        }
        centre.IsVisible = items.Count == 0;
    }

    // Filters and search

    internal void Choose(string key, string value)
    {
        if (value.Length == 0) filters.Remove(key); else filters[key] = value;
        SyncSearch();
        BuildSidebar();
        _ = ReloadAsync();
    }

    /// <summary>The runs API takes branch, event, actor, status, created and head_sha only; other words match the title, workflow, branch and actor of each page.</summary>
    internal static (Dictionary<string, string> Qualifiers, string Text) ParseSearch(string text)
    {
        var found = new Dictionary<string, string>();
        var free = new List<string>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':');
            if (colon > 0 && colon < token.Length - 1 && Qualifiers.Contains(token[..colon].ToLowerInvariant())) found[token[..colon].ToLowerInvariant()] = token[(colon + 1)..];
            else free.Add(token);
        }
        return (found, string.Join(' ', free));
    }

    internal void ApplySearch(string text)
    {
        var (found, free) = ParseSearch(text);
        foreach (var key in Qualifiers) filters.Remove(key);
        filters.Remove("q");
        foreach (var (key, value) in found) filters[key] = value;
        if (free.Length > 0) filters["q"] = free;
        _ = ReloadAsync();
    }

    private void ApplySearch() => ApplySearch(search.Text ?? "");

    private void SyncSearch() =>
        search.Text = string.Join(' ', Qualifiers.Where(filters.ContainsKey).Select(key => key + ":" + filters[key]).Concat(filters.TryGetValue("q", out var q) ? [q] : []));

    private Control Dropdown(GitHubFilter filter)
    {
        var value = filter.Key == "workflow" ? filter.Value : filters.GetValueOrDefault(filter.Key, "");
        var button = new Button
        {
            Name = "GitHubChip" + filter.Label,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4),
            Content = Ui.Text(filter.Label + (value.Length > 0 ? ": " + value : "") + " ▾", Ui.TextBrush, 13)
        };
        var flyout = new MenuFlyout();
        flyout.Items.Add(Ui.Menu("Clear " + filter.Label.ToLowerInvariant(), () => Choose(filter.Key, "")));
        foreach (var option in filter.Options) flyout.Items.Add(Ui.Menu(option, () => Choose(filter.Key, option)));
        button.Flyout = flyout;
        return button;
    }

    // Sidebar

    private bool Selected(string id) => id.Length == 0
        ? !filters.ContainsKey("workflow")
        : workflows.FirstOrDefault(w => w.Id == filters.GetValueOrDefault("workflow", "\0") || w.Name == filters.GetValueOrDefault("workflow", "\0")) is { } known && known.Id == id;

    private void BuildSidebar()
    {
        sidebarItems.Children.Clear();
        sidebarItems.Children.Add(SideItem("All workflows", "GitHubWorkflowAll", GitHubIcons.PlayCircle, Selected(""), false, () => Choose("workflow", "")));
        sidebarItems.Children.Add(new Border { Height = 1, Background = Ui.BorderBrush, Margin = new Thickness(0, 8) });
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 4) };
        label.Children.Add(Ui.Icon(GitHubIcons.NodeTree, Ui.Muted, 16)); label.Children.Add(Ui.Text("Workflows", Ui.Muted, 13));
        sidebarItems.Children.Add(label);
        foreach (var workflow in workflows)
            sidebarItems.Children.Add(SideItem(workflow.Name, "GitHubWorkflow" + workflow.Id, null, Selected(workflow.Id), workflow.State != "active", () => Choose("workflow", workflow.Id)));

        var management = new StackPanel { Name = "GitHubManagementItems", IsVisible = managementOpen };
        foreach (var (name, path) in new[] { ("Caches", "/actions/caches"), ("Runners", "/actions/runners"), ("Usage metrics", "/actions/metrics/usage") })
            management.Children.Add(SideItem(name, "GitHubManagement" + name.Replace(" ", ""), null, false, false, () => GitHubRuns.Open(repoUrl + path)));
        var managementHead = SideItem("Management " + (managementOpen ? "▾" : "▸"), "GitHubManagement", GitHubIcons.Tools, false, false, () => { managementOpen = !managementOpen; BuildSidebar(); });
        var collapse = Ui.Button("Collapse sidebar", () => SetSidebar(false), GitHubIcons.LayoutLeft);
        collapse.Name = "GitHubCollapseSidebar"; collapse.Margin = new Thickness(8); collapse.HorizontalAlignment = HorizontalAlignment.Stretch;
        var bottom = new StackPanel { Margin = new Thickness(8, 0), Children = { new Border { Height = 1, Background = Ui.BorderBrush }, managementHead, management, collapse } };
        if (sidebar.Child is Grid old) old.Children.Clear();
        var grid = new Grid { RowDefinitions = new("*,Auto") };
        grid.Children.Add(sidebarScroll);
        Grid.SetRow(bottom, 1); grid.Children.Add(bottom);
        sidebar.Child = grid;
        collapsed.Child = new Border { Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Top, Child = Ui.IconButton(GitHubIcons.LayoutLeft, "Expand sidebar", () => SetSidebar(true)) };
    }

    internal void SetSidebar(bool open) { sidebar.IsVisible = open; collapsed.IsVisible = !open; }

    private static Control SideItem(string text, string name, string? icon, bool selected, bool muted, Action click)
    {
        var row = new Grid { ColumnDefinitions = new("Auto,*") };
        var bar = new Border { Width = 2, Background = selected ? Ui.Accent : Brushes.Transparent, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2) };
        var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 4) };
        if (icon is not null) inner.Children.Add(Ui.Icon(icon, Ui.TextBrush, 16));
        var label = Ui.Text(text, muted ? Ui.Muted : Ui.TextBrush, 14);
        inner.Children.Add(label);
        row.Children.Add(bar); Grid.SetColumn(inner, 1); row.Children.Add(inner);
        var border = new Border { Name = name, Child = row, Background = selected ? Ui.Selected : Brushes.Transparent, CornerRadius = new CornerRadius(4), Cursor = new Cursor(StandardCursorType.Hand) };
        border.PointerEntered += (_, _) => { label.TextDecorations = TextDecorations.Underline; label.Foreground = Ui.Accent; };
        border.PointerExited += (_, _) => { label.TextDecorations = null; label.Foreground = muted ? Ui.Muted : Ui.TextBrush; };
        border.PointerPressed += (_, e) => { if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) click(); };
        return border;
    }

    // Rows

    private Control FooterRow(Footer footer)
    {
        var text = Ui.Text(footer.Text, Ui.Muted, 13);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border { Name = "GitHubRunsFooter", Padding = new Thickness(12), Child = text };
    }

    private Control RunRow(GitHubRun run)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,160,200,Auto"), Margin = new Thickness(16, 12), Name = "GitHubRunGrid" };
        var icon = Ui.Icon(GitHubRuns.Glyph(run.State), GitHubRuns.Tone(run.State), 16);
        icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 2, 12, 0);
        grid.Children.Add(icon);

        var heading = Ui.Text(run.Title.Length > 0 ? run.Title : run.Workflow, Ui.TextBrush, 16);
        heading.TextWrapping = TextWrapping.Wrap; heading.MaxLines = 2; heading.TextTrimming = TextTrimming.CharacterEllipsis; heading.Name = "GitHubRunTitle";
        heading.Cursor = new Cursor(StandardCursorType.Hand);
        heading.PointerEntered += (_, _) => { heading.TextDecorations = TextDecorations.Underline; heading.Foreground = Ui.Accent; };
        heading.PointerExited += (_, _) => { heading.TextDecorations = null; heading.Foreground = Ui.TextBrush; };
        var meta = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var workflow = Ui.Text(run.Workflow, Ui.Muted, 12); workflow.FontWeight = FontWeight.SemiBold;
        meta.Children.Add(workflow);
        meta.Children.Add(Ui.Text(" #" + run.Number.ToString(CultureInfo.InvariantCulture) + ": ", Ui.Muted, 12));
        Trigger(meta, run);
        var text = new StackPanel { Children = { heading, meta } };
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        if (run.Branch.Length > 0)
        {
            var branch = Ui.Text(run.Branch, Ui.TextBrush, 12); branch.FontFamily = Ui.CodeFont;
            var chip = Ui.Chip(branch); chip.HorizontalAlignment = HorizontalAlignment.Left; chip.VerticalAlignment = VerticalAlignment.Top; chip.MaxWidth = 144; chip.Name = "GitHubRunBranch";
            Grid.SetColumn(chip, 2); grid.Children.Add(chip);
        }

        var when = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 0) };
        var started = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        started.Children.Add(Ui.Icon(GitHubIcons.Calendar, Ui.Muted, 16)); started.Children.Add(Ui.Text(GitHubRuns.Started(run.Started, now(), zone), Ui.Muted, 12));
        when.Children.Add(started);
        if (run.Duration.Length > 0)
        {
            var took = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            took.Children.Add(Ui.Icon(GitHubIcons.Timer, Ui.Muted, 16)); took.Children.Add(Ui.Text(run.Duration, Ui.Muted, 12));
            when.Children.Add(took);
        }
        Grid.SetColumn(when, 3); grid.Children.Add(when);

        var file = run.WorkflowPath.Split('@')[0];
        if (file.Length > 0 && !file.StartsWith("dynamic/", StringComparison.Ordinal) && repoUrl.Length > 0)
        {
            var more = new Button { Name = "GitHubRunMore", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4), Content = Ui.Icon(GitHubIcons.MoreHorizontal, Ui.Muted, 16), VerticalAlignment = VerticalAlignment.Top };
            var flyout = new MenuFlyout();
            flyout.Items.Add(Ui.Menu("View workflow file", () => GitHubRuns.Open(repoUrl + "/blob/HEAD/" + file)));
            more.Flyout = flyout;
            Grid.SetColumn(more, 4); grid.Children.Add(more);
        }

        var border = new Border { Name = "GitHubRun" + run.Id, Child = grid, Background = Brushes.Transparent, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Cursor = new Cursor(StandardCursorType.Hand) };
        border.PointerPressed += (_, e) => { if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) OpenRun(run); };
        return border;
    }

    private void Trigger(Panel meta, GitHubRun run)
    {
        void Plain(string text) => meta.Children.Add(Ui.Text(text, Ui.Muted, 12));
        TextBlock Person() => GitHubRuns.Link(run.Actor, () => GitHubRuns.Open("https://github.com/" + run.Actor));
        switch (run.Event)
        {
            case "schedule": Plain("Scheduled"); break;
            case "workflow_dispatch": Plain("Manually run by "); meta.Children.Add(Person()); break;
            case "push" when run.Sha.Length >= 7:
                Plain("Commit "); meta.Children.Add(GitHubRuns.Link(run.Sha[..7], () => GitHubRuns.Open(repoUrl + "/commit/" + run.Sha))); Plain(" pushed by "); meta.Children.Add(Person()); break;
            case "pull_request" or "pull_request_target": Plain("Pull request by "); meta.Children.Add(Person()); break;
            default: Plain(run.Event.Replace('_', ' ') + " by "); meta.Children.Add(Person()); break;
        }
    }

    private void OpenRun(GitHubRun run)
    {
        RunOpened?.Invoke(run);
        if (context is null) return;
        _ = OpenAsync(run);
    }

    private async Task OpenAsync(GitHubRun run)
    {
        try { await context!.OpenDocumentAsync(workspace, GitHubContract.Scheme, "run:" + run.Id, "Run " + run.Title); }
        catch (Exception error) when (error is not OperationCanceledException) { context!.Notify(PluginNotificationKind.Error, "Couldn't open GitHub", error.Message); }
    }
}