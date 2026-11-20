using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// One GitHub tab. Ids: <c>issues</c> and <c>pulls</c> (a list that opens a conversation in place), <c>settings</c>,
/// and <c>issue:N</c> or <c>pull:N</c> (one conversation only). It refreshes on a timer while shown; the host answers
/// "unchanged" when nothing moved, so a quiet conversation costs one small call.
/// </summary>
internal sealed class GitHubTab : Border
{
    public const string Issues = "issues", Pulls = "pulls", Settings = "settings";

    internal static readonly IBrush Open = new SolidColorBrush(Color.Parse("#238636"));
    internal static readonly IBrush Done = new SolidColorBrush(Color.Parse("#8957e5"));
    internal static readonly IBrush Closed = new SolidColorBrush(Color.Parse("#da3633"));
    internal static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#6e7681"));

    private readonly IPluginUIContext context;
    private readonly string workspace;
    private readonly bool standalone;
    private readonly GitHubPage page = new();
    private ScrollViewer scroll => page.Scroll;
    private readonly DispatcherTimer poll = new();
    private readonly TextBox queryBox = new() { Name = "GitHubQuery", PlaceholderText = "Search all issues", MinWidth = 280, Text = "is:open" };
    private IReadOnlyList<string> authors = [];
    private IReadOnlyList<GitHubLabel> repoLabels = [];
    private readonly GitHubLinker linker;
    private readonly List<GitHubEvent> pending = [];
    private GitHubComposer? composer;
    private GitHubDetail? shown;
    private string repoSlug = "";
    private readonly string kind;
    private int? number;
    private int limit = 25;
    private string? revision;
    private int generation;
    private CancellationTokenSource? lifetime;
    private readonly GitHubFreshness freshness;

    internal static bool Handles(string id) => id is Issues or Pulls or Settings || id.StartsWith("issue:", StringComparison.Ordinal) || id.StartsWith("pull:", StringComparison.Ordinal);

    public GitHubTab(IPluginUIContext context, string workspace, string id)
    {
        this.context = context;
        this.workspace = workspace;
        Name = "GitHubTab";
        var parts = id.Split(':', 2);
        standalone = parts.Length == 2 && int.TryParse(parts[1], out _);
        kind = parts[0] switch { "pull" or Pulls => Pulls, Settings => Settings, _ => Issues };
        if (standalone) number = int.Parse(parts[1]);
        queryBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { limit = 25; revision = null; Refresh(); } };
        linker = new(() => repoSlug, HandleLink);
        freshness = new(page, Refresh);
        Child = freshness;
        poll.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { lifetime = new(); Interval(); poll.Start(); revision = null; Refresh(); };
        DetachedFromVisualTree += (_, _) => { poll.Stop(); lifetime?.Cancel(); generation++; };
        page.Show(null, Ui.Status("Loading…"));
    }

    private bool HandleLink(string url)
    {
        if (GitHubLinks.Parse(url, repoSlug) is not { } target) return false;
        var parts = target.Id.Split(':', 2);
        if (!standalone && parts.Length == 2 && (parts[0] == "issue" && kind == Issues || parts[0] == "pull" && kind == Pulls))
        {
            number = int.Parse(parts[1]); revision = null; pending.Clear(); Refresh();
        }
        else _ = OpenLinkedAsync(target.Id, target.Title);
        return true;
    }

    private async Task OpenLinkedAsync(string id, string title)
    {
        try { await context.OpenDocumentAsync(workspace, GitHubContract.Scheme, id, title); }
        catch (Exception error) when (error is not OperationCanceledException) { context.Notify(PluginNotificationKind.Error, "Couldn't open GitHub", error.Message); }
    }

    private void Interval() => poll.Interval = TimeSpan.FromSeconds(Math.Clamp(context.Settings<GitHubSettings>().PollSeconds, 15, 600));

    private void Refresh()
    {
        Interval();
        var mine = ++generation;
        var token = lifetime?.Token ?? CancellationToken.None;
        _ = kind switch
        {
            Settings => LoadSettingsAsync(mine, token),
            _ when number is { } n => LoadDetailAsync(n, mine, token),
            _ => LoadListAsync(mine, token)
        };
    }

    private async Task<T?> CallAsync<T>(Func<CancellationToken, Task<T>> call, int mine, CancellationToken token) where T : class
    {
        try
        {
            var result = await Task.Run(() => call(token), token);
            return mine == generation && !token.IsCancellationRequested ? result : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) when (mine == generation) { Show(Message(error.Message)); return null; }
    }

    private async Task LoadListAsync(int mine, CancellationToken token)
    {
        var query = queryBox.Text ?? "";
        var current = revision;
        var list = await CallAsync(async ct => await context.RequestAsync(GitHubContract.List, new(workspace, kind, query, limit, current), ct).AsTask(), mine, token);
        if (list is null) return;
        freshness.Apply(list.Cached);
        if (list.Problem is not null) { Show(Message(list.Problem)); return; }
        if (list.Unchanged) return;
        repoSlug = list.Repo;
        revision = list.Revision;
        RenderList(list);
    }

    internal void RenderList(GitHubList list)
    {
        var offset = scroll.Offset;
        var root = new StackPanel { Spacing = 0, Margin = new Thickness(24, 0, 24, 16), MaxWidth = 1012 };
        var header = new StackPanel { Spacing = 0, Margin = new Thickness(24, 16, 24, 0), MaxWidth = 1012 };
        var title = Ui.Text(kind == Pulls ? "Pull requests" : "Issues", Ui.TextBrush, 22);
        title.FontWeight = FontWeight.SemiBold;
        title.Margin = new Thickness(0, 0, 0, 12);
        header.Children.Add(title);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 12) };
        // The box outlives every rebuild, so it leaves the page it was on before it joins the new one.
        (queryBox.Parent as Panel)?.Children.Remove(queryBox);
        bar.Children.Add(queryBox);
        bar.Children.Add(Ui.Button("Search", () => { limit = 25; revision = null; Refresh(); }, GitHubIcons.Search));
        header.Children.Add(bar);
        authors = [.. list.Items.Select(item => item.Author).Distinct().Order()];
        repoLabels = list.RepoLabels;
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        chips.Children.Add(Chip("Author", "author", [.. authors.Select(a => (a, a))]));
        chips.Children.Add(Chip("Label", "label", [.. repoLabels.Select(l => (l.Name, l.Name.Contains(' ') ? "\"" + l.Name + "\"" : l.Name))]));
        chips.Children.Add(Chip("Sort", "sort", [("Newest", "created-desc"), ("Oldest", "created-asc"), ("Most commented", "comments-desc"), ("Least commented", "comments-asc"), ("Recently updated", "updated-desc"), ("Least recently updated", "updated-asc")]));
        header.Children.Add(chips);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(12, 8) };
        head.Children.Add(Toggle($"{list.Open} Open", "is:open", Open));
        head.Children.Add(Toggle($"{list.Closed} Closed", "is:closed", Done));
        var frame = new StackPanel { Name = "GitHubRows" };
        frame.Children.Add(new Border { Background = Ui.Elevated, Child = head, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1) });
        if (list.Items.Count == 0) frame.Children.Add(new Border { Padding = new Thickness(24), Child = Ui.Text("No results matched your search.") });
        foreach (var item in list.Items) frame.Children.Add(Row(item));
        root.Children.Add(new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = frame });
        if (list.HasMore)
        {
            var more = Ui.Button("Show more", () => { limit += 25; revision = null; Refresh(); });
            more.Margin = new Thickness(0, 12, 0, 0);
            more.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(more);
        }
        page.Show(header, root);
        Dispatcher.UIThread.Post(() => scroll.Offset = offset, DispatcherPriority.Loaded);
    }

    private Control Chip(string label, string qualifier, IReadOnlyList<(string Label, string Value)> options)
    {
        var current = System.Text.RegularExpressions.Regex.Match(queryBox.Text ?? "", "(^|\\s)" + qualifier + ":(\"[^\"]*\"|\\S+)");
        var chipText = Ui.Text(label + (current.Success ? ": " + current.Groups[2].Value.Trim('"') : "") + " ▾", Ui.TextBrush, 13);
        var button = new Button { Name = "GitHubChip" + label, Content = chipText, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 8, 0) };
        HoverLink.Apply(chipText, button);
        var flyout = new MenuFlyout();
        flyout.Items.Add(Ui.Menu("Clear " + label.ToLowerInvariant(), () => SetQualifier(qualifier, null)));
        foreach (var option in options) flyout.Items.Add(Ui.Menu(option.Label, () => SetQualifier(qualifier, option.Value)));
        button.Flyout = flyout;
        return button;
    }

    private void SetQualifier(string qualifier, string? value)
    {
        var rest = System.Text.RegularExpressions.Regex.Replace(queryBox.Text ?? "", "(^|\\s)" + qualifier + ":(\"[^\"]*\"|\\S+)", "").Trim();
        queryBox.Text = (rest + (value is null ? "" : " " + qualifier + ":" + value)).Trim();
        limit = 25; revision = null; Refresh();
    }

    private Control Toggle(string label, string token, IBrush brush)
    {
        var active = (queryBox.Text ?? "").Contains(token, StringComparison.Ordinal);
        var text = Ui.Text(label, active ? Ui.TextBrush : Ui.Muted, 14);
        if (active) text.FontWeight = FontWeight.SemiBold;
        var button = new Button { Name = "GitHubToggle" + token.Replace(':', '-'), Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        HoverLink.Apply(text, button);
        button.Click += (_, _) =>
        {
            var rest = System.Text.RegularExpressions.Regex.Replace(queryBox.Text ?? "", @"(^|\s)is:(open|closed|merged)(?=\s|$)", "").Trim();
            queryBox.Text = (token + " " + rest).Trim();
            limit = 25; revision = null; Refresh();
        };
        return button;
    }

    private Control Row(GitHubItem item)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(12, 8) };
        grid.Children.Add(StateIcon(item.State, item.Draft, kind == Pulls));
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 0, 0) };
        var titleRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var title = Ui.Text(item.Title, Ui.TextBrush, 14);
        title.Name = "GitHubTitle" + item.Number;
        title.FontWeight = FontWeight.SemiBold;
        title.Margin = new Thickness(0, 0, 8, 0);
        titleRow.Children.Add(title);
        foreach (var label in item.Labels) titleRow.Children.Add(LabelChip(label));
        if (item.Checks != "none") titleRow.Children.Add(Ui.Text(item.Checks switch { "passing" => "checks passing", "failing" => "checks failing", _ => "checks running" }, Muted(item.Checks), 12));
        text.Children.Add(titleRow);
        var verb = item.State == "merged" ? "merged" : item.State == "closed" ? "closed" : "opened";
        text.Children.Add(Ui.Text($"#{item.Number} · {item.Author} {verb} {Ago(item.ClosedAt ?? item.CreatedAt)}" + (item.Review == "approved" ? " · Approved" : item.Review == "changes-requested" ? " · Changes requested" : ""), Ui.Muted, 12));
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Top };
        if (item.Assignees.Count > 0) right.Children.Add(GitHubAvatars.Stack(item.Assignees));
        if (item.Comments > 0)
        {
            var comments = Ui.Row(GitHubIcons.Chat, item.Comments.ToString());
            comments.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(comments);
        }
        if (right.Children.Count > 0) { Grid.SetColumn(right, 2); grid.Children.Add(right); }
        var row = new Border { Name = "GitHubRow" + item.Number, Child = grid, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
        HoverLink.Apply(title, row, () => { number = item.Number; revision = null; Refresh(); });
        row.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(row).Properties;
            if (point.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)) _ = OpenTabAsync(item);
            else if (point.IsLeftButtonPressed) { number = item.Number; revision = null; Refresh(); }
        };
        return row;
    }

    private async Task OpenTabAsync(GitHubItem item)
    {
        var pull = kind == Pulls;
        try { await context.OpenDocumentAsync(workspace, GitHubContract.Scheme, (pull ? "pull:" : "issue:") + item.Number, (pull ? "PR #" : "Issue #") + item.Number); }
        catch (Exception error) when (error is not OperationCanceledException) { context.Notify(PluginNotificationKind.Error, "Couldn't open GitHub", error.Message); }
    }

    private async Task LoadDetailAsync(int n, int mine, CancellationToken token)
    {
        var current = revision;
        var detail = await CallAsync(async ct => await context.RequestAsync(GitHubContract.Detail, new(workspace, kind, n, current), ct).AsTask(), mine, token);
        if (detail is null) return;
        freshness.Apply(detail.Cached);
        if (detail.Problem is not null) { Show(Message(detail.Problem, back: !standalone)); return; }
        if (detail.Unchanged) return;
        revision = detail.Revision;
        repoSlug = detail.Repo;
        shown = detail;
        RenderDetail(detail);
    }

    internal void RenderDetail(GitHubDetail detail)
    {
        var offset = scroll.Offset;
        var remaining = GitHubPending.Remaining(pending, detail.Timeline);
        pending.Clear(); pending.AddRange(remaining);
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(24, 16), MaxWidth = 1012, Name = "GitHubConversation" };
        if (!standalone)
        {
            var back = Ui.Button(kind == Pulls ? "Pull requests" : "Issues", () => { number = null; revision = null; Refresh(); }, GitHubIcons.ArrowLeft);
            back.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(back);
        }
        var title = Ui.Text(detail.Title + " #" + detail.Number, Ui.TextBrush, 24);
        title.TextWrapping = TextWrapping.Wrap; title.FontWeight = FontWeight.SemiBold;
        root.Children.Add(title);
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        meta.Children.Add(Badge(detail));
        var noun = detail.Kind == Pulls ? "pull request" : "issue";
        var comments = detail.Timeline.Count(e => e.Kind is "comment" or "review");
        meta.Children.Add(Ui.Text($"{detail.Author} opened this {noun} {Ago(detail.CreatedAt)} · {comments} comments", Ui.Muted, 13));
        root.Children.Add(meta);
        if (detail.Pull is { } pull)
        {
            root.Children.Add(Ui.Text($"{detail.Author} wants to merge {detail.Timeline.Count(e => e.Kind == "commit")} commits into {pull.BaseRef} from {pull.HeadRef} · +{pull.Additions} -{pull.Deletions} in {pull.ChangedFiles} files · merge: {pull.Mergeability} · reviews: {pull.Review}", Ui.Muted, 13));
            foreach (var check in pull.CheckRuns) root.Children.Add(Ui.Text((check.State == "passing" ? "✓ " : check.State == "failing" ? "✗ " : "● ") + check.Name, Muted(check.State), 12));
        }
        if (detail.Labels.Count > 0 || detail.Assignees.Count > 0 || detail.Milestone is not null)
        {
            var side = new WrapPanel();
            foreach (var label in detail.Labels) side.Children.Add(LabelChip(label));
            if (detail.Assignees.Count > 0) side.Children.Add(Ui.Text("Assigned to " + string.Join(", ", detail.Assignees), Ui.Muted, 12));
            if (detail.People.Count > 0) { var people = GitHubAvatars.Stack(detail.People); people.Margin = new Thickness(8, 0); side.Children.Add(people); }
            if (detail.Milestone is not null) side.Children.Add(Ui.Text("  Milestone " + detail.Milestone, Ui.Muted, 12));
            root.Children.Add(side);
        }
        root.Children.Add(Card(detail.Author, detail.CreatedAt, "", detail.Body.Length == 0 ? "No description provided." : detail.Body, null, linker));
        if (detail.TimelineProblem is { } timelineProblem) root.Children.Add(Message(timelineProblem));
        foreach (var e in detail.Timeline.Concat(pending)) root.Children.Add(Event(e));
        if (detail.Truncated) root.Children.Add(Ui.Text("The conversation is longer than this tab loads. The rest is on GitHub.", Ui.Muted, 12));
        var id = detail.Number;
        if (composer is null || composerNumber != id)
        {
            composer = new GitHubComposer($"{workspace}|{detail.Kind}|{id}", detail.State != "merged", async (action, body) =>
                await Task.Run(async () => await context.RequestAsync(GitHubContract.Post, new GitHubPostParams(workspace, detail.Kind, id, action, body), lifetime?.Token ?? default).AsTask()),
                Posted, linker);
            composerNumber = id;
        }
        composer.Detach();
        composer.SetState(detail.State);
        root.Children.Add(composer);
        Show(root);
        Dispatcher.UIThread.Post(() => scroll.Offset = offset, DispatcherPriority.Loaded);
    }

    private int composerNumber;

    private void Posted(GitHubEvent? posted, string? state)
    {
        if (posted is not null) pending.Add(posted);
        if (shown is not null)
        {
            if (state is not null) shown = shown with { State = state == "open" ? "open" : "closed" };
            RenderDetail(shown);
        }
        revision = null;
        Refresh();
    }

    private Control Event(GitHubEvent e)
    {
        if (e.Kind == "comment") return Card(e.Author, e.At, e.Detail is "" or "NONE" ? "" : e.Detail.ToLowerInvariant(), e.Body, null, linker);
        if (e.Kind == "review")
            return Card(e.Author, e.At, e.Detail switch { "approved" => "approved these changes", "changes-requested" => "requested changes", "dismissed" => "review dismissed", _ => "reviewed" }, e.Body, e.Comments, linker);
        var line = new WrapPanel { Margin = new Thickness(16, 0, 0, 0) };
        line.Children.Add(Ui.Icon(Glyph(e.Kind), Ui.Muted, 14));
        var phrase = e.Kind switch
        {
            "commit" => $"{e.Author} committed {e.Detail} {e.Body}",
            "labeled" => $"{e.Author} added the",
            "unlabeled" => $"{e.Author} removed the",
            "closed" => $"{e.Author} closed this",
            "reopened" => $"{e.Author} reopened this",
            "merged" => $"{e.Author} merged this into {e.Detail}",
            "assigned" => $"{e.Author} assigned {e.Detail}",
            "unassigned" => $"{e.Author} unassigned {e.Detail}",
            "renamed" => $"{e.Author} renamed this from {e.Detail}",
            "milestoned" => $"{e.Author} added this to the {e.Detail} milestone",
            "referenced" => $"{e.Author} mentioned this in {e.Detail}",
            "review-requested" => $"{e.Author} requested a review from {e.Detail}",
            "ready" => $"{e.Author} marked this ready for review",
            "draft" => $"{e.Author} marked this as a draft",
            "force-pushed" => $"{e.Author} force-pushed the branch",
            "branch-deleted" => $"{e.Author} deleted the {e.Detail} branch",
            _ => e.Author + " " + e.Kind
        };
        var text = Ui.Text(" " + phrase, Ui.Muted, 13);
        text.TextTrimming = TextTrimming.None; text.TextWrapping = TextWrapping.Wrap;
        line.Children.Add(text);
        if (e.Kind is "labeled" or "unlabeled") { line.Children.Add(LabelChip(new(e.Detail, e.Color))); line.Children.Add(Ui.Text(" label", Ui.Muted, 13)); }
        line.Children.Add(Ui.Text("  " + Ago(e.At), Ui.Muted, 12));
        return line;
    }

    private static string Glyph(string kind) => kind switch
    {
        "commit" => GitHubIcons.GitCommit,
        "merged" => GitHubIcons.GitMerge,
        "closed" => GitHubIcons.CheckboxCircle,
        "reopened" => GitHubIcons.RecordCircle,
        "labeled" or "unlabeled" => GitHubIcons.PriceTag,
        "referenced" => GitHubIcons.Link,
        _ => GitHubIcons.CheckboxBlankCircle
    };

    internal static Control Card(string author, string at, string note, string body, IReadOnlyList<GitHubReviewComment>? inline, GitHubLinker? linker = null)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 8) };
        var name = Ui.Text(author, Ui.TextBrush, 13); name.FontWeight = FontWeight.SemiBold;
        header.Children.Add(name);
        header.Children.Add(Ui.Text((note.Length > 0 ? note + " " : "commented ") + Ago(at), Ui.Muted, 13));
        var content = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        if (body.Length > 0) content.Children.Add(Body(body, linker));
        if (inline is not null)
            foreach (var comment in inline)
            {
                content.Children.Add(Ui.Text(comment.Path, Ui.Muted, 12));
                content.Children.Add(Body(comment.Body, linker));
            }
        var frame = new StackPanel();
        frame.Children.Add(new Border { Background = Ui.Elevated, Child = header, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1) });
        if (content.Children.Count > 0) frame.Children.Add(content);
        return new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = frame };
    }

    internal static Control Body(string text, GitHubLinker? linker = null) => GitHubMarkdown.Render(text, linker);

    private static Control Badge(GitHubDetail detail)
    {
        var (text, brush) = detail.State switch { "merged" => ("Merged", Done), "closed" => ("Closed", detail.Kind == Pulls ? Closed : Done), _ => (detail.Draft ? "Draft" : "Open", detail.Draft ? Neutral : Open) };
        return new Border { Background = brush, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3), Child = Ui.Text(text, Brushes.White, 13) };
    }

    internal static Control LabelChip(GitHubLabel label)
    {
        var color = label.Color.Length == 6 ? Color.Parse("#" + label.Color) : Colors.Gray;
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        return new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(7, 1),
            Margin = new Thickness(0, 0, 4, 0),
            Child = Ui.Text(label.Name, luminance > 0.55 ? Brushes.Black : Brushes.White, 12)
        };
    }

    private static Control StateIcon(string state, bool draft, bool pull)
    {
        var (icon, brush) = (pull, state) switch
        {
            (true, "merged") => (GitHubIcons.GitMerge, Done),
            (true, "closed") => (GitHubIcons.GitPullRequest, Closed),
            (true, _) => (GitHubIcons.GitPullRequest, draft ? Neutral : Open),
            (false, "open") => (GitHubIcons.RecordCircle, Open),
            _ => (GitHubIcons.CheckboxCircle, Done)
        };
        var control = Ui.Icon(icon, brush, 16);
        control.VerticalAlignment = VerticalAlignment.Top; control.Margin = new Thickness(0, 2, 0, 0);
        return control;
    }

    internal static IBrush Muted(string state) => state switch { "passing" => Open, "failing" => Closed, _ => Neutral };

    internal static string Ago(string iso) => DateTimeOffset.TryParse(iso, out var time) ? Ui.RelativeTime(time, DateTimeOffset.UtcNow) : "";

    private void Show(Control content) => page.Show(null, content);

    private Control Message(string text, bool back = false, bool retry = true)
    {
        var panel = new StackPanel { Name = "GitHubMessage", Spacing = 8, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Ui.Status(text, Ui.Muted, 14));
        if (back || retry)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            if (back) buttons.Children.Add(Ui.Button("Back", () => { number = null; revision = null; Refresh(); }));
            if (retry) buttons.Children.Add(Ui.Button("Retry", () => { revision = null; Refresh(); }));
            panel.Children.Add(buttons);
        }
        return panel;
    }

    private async Task LoadSettingsAsync(int mine, CancellationToken token)
    {
        var info = await CallAsync(async ct => await context.RequestAsync(GitHubContract.Info, new(workspace), ct).AsTask(), mine, token);
        if (info is null) return;
        var root = new StackPanel { Spacing = 8, Margin = new Thickness(24, 16), MaxWidth = 720, Name = "GitHubSettingsTab" };
        var title = Ui.Text("GitHub settings", Ui.TextBrush, 22); title.FontWeight = FontWeight.SemiBold;
        root.Children.Add(title);
        root.Children.Add(Ui.Text(info.SignedIn ? "Connected" : "Not connected", info.SignedIn ? Open : Closed, 14));
        root.Children.Add(Ui.Text("Account: " + info.Account, Ui.Muted, 13));
        root.Children.Add(Ui.Text("Repository: " + (info.Repo.Length > 0 ? info.Repo : info.Problem ?? "none for this workspace"), Ui.Muted, 13));
        root.Children.Add(Ui.Text($"Refresh every {info.PollSeconds} seconds while a GitHub tab is shown.", Ui.Muted, 13));
        var interval = new NumericUpDown { Minimum = 15, Maximum = 600, Value = info.PollSeconds, Increment = 15, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        interval.ValueChanged += async (_, e) =>
        {
            if (e.NewValue is { } value && (int)value != info.PollSeconds)
                try { await context.UpdateSettingsAsync(context.Settings<GitHubSettings>() with { PollSeconds = (int)value }); } catch (Exception error) when (error is not OperationCanceledException) { }
        };
        root.Children.Add(interval);
        root.Children.Add(new GitHubCacheSettings(context));
        root.Children.Add(Ui.Text("tereditor uses the GitHub CLI on this computer and stores no credentials. This pane reads only; it cannot post.", Ui.Muted, 13));
        Show(root);
    }
}