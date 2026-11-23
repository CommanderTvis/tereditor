using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

internal enum ContributionMetric { Commits, Additions, Deletions }

/// <summary>One contributor's totals in the chosen window, with the rank that window gives.</summary>
internal sealed record RankedContributor(GitHubContributor Author, int Rank, long Commits, long Additions, long Deletions);

/// <summary>What the page shows: the statistics, the metric and the window of weeks. Controls read it and redraw when it changes.</summary>
internal sealed class ContributorsModel
{
    internal static readonly (string Name, int Weeks)[] Periods = [("Last month", 5), ("Last 3 months", 13), ("Last 6 months", 26), ("Last year", 52), ("All time", int.MaxValue)];

    private readonly Dictionary<ContributionMetric, double[]> totals = [];

    public GitHubContributors Data { get; private set; } = new(false, "", "", false, [], []);

    public ContributionMetric Metric { get; private set; }

    public string Period { get; private set; } = "Last 3 months";

    public int Start { get; private set; }

    public int End { get; private set; }

    public event Action? Changed;

    public void SetData(GitHubContributors data)
    {
        Data = data; totals.Clear();
        ApplyPeriod(Period);
    }

    public void SetMetric(ContributionMetric metric) { Metric = metric; Changed?.Invoke(); }

    public void SetPeriod(string name) { ApplyPeriod(name); Changed?.Invoke(); }

    public void SetWindow(int start, int end) { Start = start; End = end; Changed?.Invoke(); }

    private void ApplyPeriod(string name)
    {
        Period = name;
        var weeks = Periods.First(p => p.Name == name).Weeks;
        End = Data.Weeks.Count; Start = (int)Math.Max(0, (long)End - weeks);
    }

    public static IReadOnlyList<int> Series(GitHubContributor author, ContributionMetric metric) =>
        metric switch { ContributionMetric.Additions => author.Additions, ContributionMetric.Deletions => author.Deletions, _ => author.Commits };

    /// <summary>Weekly totals of the whole repository for the metric.</summary>
    public double[] Totals(ContributionMetric metric)
    {
        if (totals.TryGetValue(metric, out var cached)) return cached;
        var sum = new double[Data.Weeks.Count];
        foreach (var author in Data.Authors)
        {
            var series = Series(author, metric);
            for (var i = 0; i < sum.Length; i++) sum[i] += series[i];
        }
        return totals[metric] = sum;
    }

    /// <summary>Contributors with something in the window, most first in the chosen metric.</summary>
    public IReadOnlyList<RankedContributor> Ranked()
    {
        long Sum(IReadOnlyList<int> series) { long total = 0; for (var i = Start; i < End; i++) total += series[i]; return total; }
        var all = Data.Authors.Select(a => (Author: a, C: Sum(a.Commits), A: Sum(a.Additions), D: Sum(a.Deletions)))
            .Where(x => (Metric switch { ContributionMetric.Additions => x.A, ContributionMetric.Deletions => x.D, _ => x.C }) > 0)
            .OrderByDescending(x => Metric switch { ContributionMetric.Additions => x.A, ContributionMetric.Deletions => x.D, _ => x.C })
            .ThenBy(x => x.Author.Login, StringComparer.OrdinalIgnoreCase).ToList();
        return [.. all.Select((x, i) => new RankedContributor(x.Author, i + 1, x.C, x.A, x.D))];
    }

    public string WeekLabel(int index) => Date(index).ToString("MMM d", CultureInfo.InvariantCulture);

    public DateTimeOffset Date(int index) => DateTimeOffset.FromUnixTimeSeconds(Data.Weeks[Math.Clamp(index, 0, Data.Weeks.Count - 1)]);
}

/// <summary>
/// The Contributors tab, laid out like GitHub's page: a header with the period and metric, a bar chart of the whole repository with a
/// range selector under it, then a two-column grid of contributor cards. Everything sits in one virtualized list, so only the rows in
/// view exist as controls. Statistics come from the host once GitHub has computed them: the tab asks again after 2, 4, 8 and 16 seconds.
/// </summary>
internal sealed class GitHubContributorsTab : Border
{
    internal static TimeSpan[] Backoff { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)];

    private abstract record PageItem;
    private sealed record HeaderItem : PageItem;
    private sealed record ChartItem : PageItem;
    private sealed record CardsItem(RankedContributor Left, RankedContributor? Right) : PageItem;

    private readonly IPluginUIContext? context;
    private readonly string workspace;
    private readonly Func<string?, CancellationToken, Task<GitHubContributors>>? fetch;
    private readonly ContributorsModel model = new();
    private readonly DispatcherTimer poll = new();
    private readonly AvaloniaList<object> items = [];
    private string? revision;
    private int generation;
    private bool waiting;
    private CancellationTokenSource? lifetime;

    public GitHubContributorsTab(IPluginUIContext? context, string workspace, Func<string?, CancellationToken, Task<GitHubContributors>>? fetch = null)
    {
        this.context = context; this.workspace = workspace; this.fetch = fetch;
        Name = "GitHubContributorsTab";
        Child = Message("Loading…");
        poll.Tick += (_, _) => { if (!waiting) Refresh(); };
        AttachedToVisualTree += (_, _) =>
        {
            lifetime = new();
            poll.Interval = TimeSpan.FromSeconds(Math.Clamp(context?.Settings<GitHubSettings>().PollSeconds ?? 60, 15, 600)); poll.Start();
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => { poll.Stop(); lifetime?.Cancel(); generation++; waiting = false; };
    }

    internal ContributorsModel Model => model;

    private void Refresh()
    {
        var mine = ++generation;
        var token = lifetime?.Token ?? CancellationToken.None;
        _ = LoadAsync(mine, token);
    }

    private Task<GitHubContributors> Request(string? known, CancellationToken token) =>
        fetch is not null ? fetch(known, token) : Task.Run(async () => await context!.RequestAsync(GitHubContract.ContributorStats, new GitHubContributorsParams(workspace, known), token).AsTask(), token);

    private async Task LoadAsync(int mine, CancellationToken token)
    {
        GitHubContributors result;
        try
        {
            waiting = true;
            result = await FetchAsync(Request, revision, Backoff, () => Dispatcher.UIThread.Post(() => { if (mine == generation) ShowComputing(); }), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { if (mine == generation) Child = Message(error.Message); return; }
        finally { if (mine == generation) waiting = false; }
        if (mine != generation || token.IsCancellationRequested) return;
        if (result.Unchanged) return;
        revision = result.Revision;
        Show(result);
    }

    /// <summary>Asks until GitHub has the numbers: while it answers Computing, waits one back-off step and asks again, then gives up with the last answer.</summary>
    internal static async Task<GitHubContributors> FetchAsync(Func<string?, CancellationToken, Task<GitHubContributors>> request, string? known, IReadOnlyList<TimeSpan> backoff, Action computing, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var answer = await request(known, token).ConfigureAwait(false);
            if (!answer.Computing || attempt >= backoff.Count) return answer;
            computing();
            await Task.Delay(backoff[attempt], token).ConfigureAwait(false);
        }
    }

    internal void ShowComputing() => Child = Message("GitHub is computing these numbers...");

    internal void Show(GitHubContributors data)
    {
        if (data.Problem is not null) { Child = Message(data.Problem); return; }
        if (data.Computing) { Child = Message("GitHub is still computing these numbers. Open this tab again in a minute."); return; }
        if (data.Authors.Count == 0 || data.Weeks.Count == 0) { Child = Message("GitHub has no contribution statistics for this repository."); return; }
        model.SetData(data);
        items.Clear();
        items.Add(new HeaderItem()); items.Add(new ChartItem());
        AddCards();
        var list = new ItemsControl
        {
            Name = "GitHubContributorsList",
            MaxWidth = 1012,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(24, 16),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
            {
                HeaderItem => Header(),
                ChartItem => BigChart(),
                CardsItem row => CardRow(row),
                string text => Message(text),
                _ => new Border()
            })
        };
        Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list };
    }

    /// <summary>Replaces the card rows for the current period and metric; the header and chart rows stay as they are.</summary>
    private void AddCards()
    {
        while (items.Count > 2) items.RemoveAt(items.Count - 1);
        var ranked = model.Ranked();
        var rows = new List<object>();
        for (var i = 0; i < ranked.Count; i += 2) rows.Add(new CardsItem(ranked[i], i + 1 < ranked.Count ? ranked[i + 1] : null));
        if (rows.Count == 0) rows.Add("No contributions in this period.");
        items.AddRange(rows);
    }

    private static Control Message(string text)
    {
        var line = Ui.Text(text, Ui.Muted, 14); line.TextWrapping = TextWrapping.Wrap; line.TextTrimming = TextTrimming.None; line.Margin = new Thickness(24);
        return line;
    }

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string MetricName(ContributionMetric metric) => metric.ToString();

    private void Reflow() { AddCards(); }

    private static void OpenProfile(string url)
    {
        if (url.StartsWith("https://github.com/", StringComparison.Ordinal))
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception error) when (error is not OperationCanceledException) { Console.Error.WriteLine(error); }
    }

    private Button Dropdown(string name, Func<string> current, IEnumerable<(string Label, Action Choose)> options)
    {
        var button = new Button { Name = name, Background = Brushes.Transparent, Padding = new Thickness(8, 4) };
        var flyout = new MenuFlyout();
        foreach (var (label, choose) in options) flyout.Items.Add(Ui.Menu(label, choose));
        button.Flyout = flyout;
        void Update() => button.Content = Typography.Apply(Ui.Text(current() + " ▾", Ui.TextBrush), Typography.UiAction);
        Update();
        button.AttachedToVisualTree += (_, _) => { model.Changed += Update; Update(); };
        button.DetachedFromVisualTree += (_, _) => model.Changed -= Update;
        return button;
    }

    private Button IconMenu(string icon, string tooltip, IEnumerable<(string Label, Action Choose)> options)
    {
        var button = Ui.IconButton(icon, tooltip, () => { });
        var flyout = new MenuFlyout();
        foreach (var (label, choose) in options) flyout.Items.Add(Ui.Menu(label, choose));
        button.Flyout = flyout;
        return button;
    }

    private IEnumerable<(string, Action)> PeriodOptions() => ContributorsModel.Periods.Select(p => (p.Name, (Action)(() => { model.SetPeriod(p.Name); Reflow(); })));

    private IEnumerable<(string, Action)> MetricOptions() => Enum.GetValues<ContributionMetric>().Select(m => (MetricName(m), (Action)(() => { model.SetMetric(m); Reflow(); })));

    private Control Header()
    {
        var grid = new Grid { Name = "GitHubContributorsHeader", ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 0, 0, 16) };
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(Typography.Apply(Ui.Text("Contributors", Ui.TextBrush), Typography.HeadingLg));
        var subtitle = Typography.Apply(Ui.Text("", Ui.Muted), Typography.UiMetadata);
        subtitle.TextWrapping = TextWrapping.Wrap; subtitle.TextTrimming = TextTrimming.None;
        void Update() => subtitle.Text = $"Contributions per week to {model.Data.Branch}, excluding merge commits";
        Update();
        text.Children.Add(subtitle);
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(Dropdown("GitHubPeriod", () => "Period: " + model.Period, PeriodOptions()));
        buttons.Children.Add(Dropdown("GitHubMetric", () => "Contributions: " + MetricName(model.Metric), MetricOptions()));
        Grid.SetColumn(buttons, 1); grid.Children.Add(buttons);
        return grid;
    }

    private Control BigChart()
    {
        var frame = new Border { Name = "GitHubContributorsChart", BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16) };
        var stack = new StackPanel { Spacing = 8 };
        var top = new Grid { ColumnDefinitions = new("*,Auto") };
        var heading = new StackPanel { Spacing = 2 };
        var title = Typography.Apply(Ui.Text("", Ui.TextBrush), Typography.TitleSection);
        var subtitle = Typography.Apply(Ui.Text("", Ui.Muted), Typography.UiMetadata);
        heading.Children.Add(title); heading.Children.Add(subtitle);
        top.Children.Add(heading);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(IconMenu(GitHubIcons.More, "More", MetricOptions()));
        tools.Children.Add(IconMenu(GitHubIcons.Settings, "Chart settings", PeriodOptions()));
        Grid.SetColumn(tools, 1); top.Children.Add(tools);
        stack.Children.Add(top);
        var chart = new GitHubBarChart { Name = "GitHubBigChart", Height = 220, AxisTitle = "Contributions" };
        stack.Children.Add(chart);
        var selector = new GitHubRangeSelector { Name = "GitHubRangeSelector" };
        stack.Children.Add(selector);
        frame.Child = stack;

        void Draw()
        {
            var data = model.Totals(model.Metric);
            title.Text = MetricName(model.Metric) + " over time";
            subtitle.Text = $"Weekly from {model.Date(model.Start):MMM d, yyyy} to {model.Date(model.End - 1):MMM d, yyyy}";
            chart.Set(data, model.Start, model.End, model.WeekLabel);
            selector.Set(data, model.Start, model.End);
        }
        selector.RangeChanged += (start, end, final) =>
        {
            model.SetWindow(start, end);
            if (final) Reflow();
        };
        Draw();
        void OnChanged() { Draw(); }
        frame.AttachedToVisualTree += (_, _) => model.Changed += OnChanged;
        frame.DetachedFromVisualTree += (_, _) => model.Changed -= OnChanged;
        return frame;
    }

    private Control CardRow(CardsItem row)
    {
        var grid = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 16) };
        grid.Children.Add(new GitHubContributorCard(model, row.Left, PeriodOptions(), OpenProfile));
        if (row.Right is not null)
        {
            var right = new GitHubContributorCard(model, row.Right, PeriodOptions(), OpenProfile);
            Grid.SetColumn(right, 1); grid.Children.Add(right);
        }
        return grid;
    }
}

/// <summary>One contributor's card: avatar, login, rank, the period's totals and a small weekly chart of the chosen metric.</summary>
internal sealed class GitHubContributorCard : Border
{
    private readonly ContributorsModel model;
    private readonly GitHubBarChart chart = new() { Height = 96, AxisTitle = "" };

    public GitHubContributorCard(ContributorsModel model, RankedContributor entry, IEnumerable<(string Label, Action Choose)> periods, Action<string> open)
    {
        this.model = model;
        Name = "GitHubContributorCard";
        BorderBrush = Ui.BorderBrush; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(8); Padding = new Thickness(16);
        var stack = new StackPanel { Spacing = 12 };
        var top = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 12 };
        top.Children.Add(new GitHubAvatar(entry.Author.AvatarUrl, 40) { VerticalAlignment = VerticalAlignment.Top });
        var text = new StackPanel { Spacing = 4 };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var login = Typography.Apply(Ui.Text(entry.Author.Login, Ui.Accent), Typography.TitleCard);
        login.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        login.PointerPressed += (_, _) => open(entry.Author.Url);
        name.Children.Add(login);
        name.Children.Add(Ui.Chip(Typography.Apply(Ui.Text("#" + entry.Rank, Ui.Muted), Typography.UiMetadata)));
        text.Children.Add(name);
        var line = Typography.Apply(new TextBlock { TextWrapping = TextWrapping.Wrap }, Typography.UiMetadata);
        line.Inlines!.Add(new Run(Count(entry.Commits) + (entry.Commits == 1 ? " commit  " : " commits  ")) { Foreground = Ui.Muted });
        line.Inlines.Add(new Run(Count(entry.Additions) + " ++  ") { Foreground = GitHubTab.Open });
        line.Inlines.Add(new Run(Count(entry.Deletions) + " --") { Foreground = GitHubTab.Closed });
        text.Children.Add(line);
        Grid.SetColumn(text, 1); top.Children.Add(text);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var more = Ui.IconButton(GitHubIcons.More, "More", () => { });
        var menu = new MenuFlyout();
        menu.Items.Add(Ui.Menu("Open profile on GitHub", () => open(entry.Author.Url)));
        more.Flyout = menu;
        var settings = Ui.IconButton(GitHubIcons.Settings, "Period", () => { });
        var periodMenu = new MenuFlyout();
        foreach (var (label, choose) in periods) periodMenu.Items.Add(Ui.Menu(label, choose));
        settings.Flyout = periodMenu;
        tools.Children.Add(more); tools.Children.Add(settings);
        Grid.SetColumn(tools, 2); top.Children.Add(tools);
        stack.Children.Add(top);
        chart.Set([.. ContributorsModel.Series(entry.Author, model.Metric).Select(v => (double)v)], model.Start, model.End, model.WeekLabel);
        stack.Children.Add(chart);
        Child = stack;
        AttachedToVisualTree += (_, _) => { model.Changed += OnChanged; OnChanged(); };
        DetachedFromVisualTree += (_, _) => model.Changed -= OnChanged;
    }

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private void OnChanged() => chart.SetRange(model.Start, model.End);
}