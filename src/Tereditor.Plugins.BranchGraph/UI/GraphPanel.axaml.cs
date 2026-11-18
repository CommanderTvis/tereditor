using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.BranchGraph.UI;

/// <summary>
/// The Git Graph side tool: the project's branches drawn as commit history, lane-laid, paged and windowed. Clicking a
/// commit scopes the Changes panel to it; right-clicking opens its changes or copies its hash or patch. Nothing here changes a repository.
/// </summary>
internal sealed partial class GraphPanel : UserControl
{
    // Rows kept beyond each edge of the viewport, so a fast scroll never shows the gap it opens.
    private const int Overscan = 12;
    private readonly IPluginUIContext context;
    private string workspaceId;
    private readonly TextBlock message;
    private readonly ScrollViewer scroll;
    private readonly Border body;
    private readonly StackPanel window;
    private readonly List<IDisposable> observers = [];
    private readonly SortedDictionary<int, GraphCommitRow> built = [];
    private History? history;
    private Dictionary<string, List<string>> marks = [];
    private string? projectId;
    private int generation, drawnLanes = 1, shownLanes = 1;
    private bool reading;
    // The checks per commit, null for a commit the host knows none for; answered holds what was asked, so a commit without checks is not asked again on every scroll.
    private readonly Dictionary<string, GitGraphCheck?> checks = [];
    private readonly HashSet<string> answered = [];
    private readonly HashSet<string> asking = [];
    private readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool checksFailed;

    private sealed record History(IReadOnlyList<GraphRow> Rows, IReadOnlyList<string> Carry, IReadOnlyList<GitGraphWorktree> Worktrees, bool HasMore);

    public GraphPanel(IPluginUIContext context, string workspaceId)
    {
        this.context = context;
        this.workspaceId = workspaceId;
        AvaloniaXamlLoader.Load(this);
        message = this.FindControl<TextBlock>("GraphMessage")!;
        scroll = this.FindControl<ScrollViewer>("GraphScroll")!;
        body = this.FindControl<Border>("GraphBody")!;
        window = this.FindControl<StackPanel>("GraphWindow")!;
        scroll.ScrollChanged += (_, _) => { Window(); MaybePage(); };
        settle.Tick += (_, _) => { settle.Stop(); _ = AskChecksAsync(refresh: false); };
        poll.Tick += (_, _) => _ = AskChecksAsync(refresh: true);
        AttachedToVisualTree += (_, _) =>
        {
            if (observers.Count > 0) return;
            // The project is the one this panel's own workspace belongs to, not whichever window has focus.
            observers.Add(context.WatchHost(OwnProject, (next, _) =>
            {
                projectId = next;
                history = null;
                Message("Reading history…");
                Read(null);
            }));
            // A write in the worktree can move any ref, so the history is read again from its tip, in place, because
            // tearing the list down would throw the reader back to the top mid-scroll.
            observers.Add(context.WatchHost(ProjectRevision, (_, _) => { answered.Clear(); Read(null); }));
            projectId = OwnProject(context.Host());
            Read(null);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            generation++;
            reading = false;
            settle.Stop();
            poll.Stop();
            asking.Clear();
            foreach (var observer in observers) observer.Dispose();
            observers.Clear();
        };
    }

    private string? OwnProject(PluginHostProjection host) =>
        host.Workspaces.Values.SelectMany(workspaces => workspaces).FirstOrDefault(workspace => workspace.Id == workspaceId)?.ProjectId;

    private int ProjectRevision(PluginHostProjection host) => host.Workspaces.Values.SelectMany(workspaces => workspaces)
        .Where(workspace => workspace.ProjectId == projectId).Sum(workspace => host.WorkspaceRevisions.GetValueOrDefault(workspace.Id));

    // The history is read per project, so a workspace of the same project has nothing in flight to cancel or drop; another project is refused.
    internal bool Retarget(string workspace)
    {
        var host = context.Host();
        var nextProject = host.Workspaces.Values.SelectMany(workspaces => workspaces).FirstOrDefault(item => item.Id == workspace)?.ProjectId;
        if (projectId is null || nextProject != projectId) return false;
        workspaceId = workspace;
        return true;
    }

    /// <summary>The rows read so far, the lanes the rows on screen need, and how many rows are built, for the checks.</summary>
    internal int Rows => history?.Rows.Count ?? 0;
    internal int ShownLanes => shownLanes;
    internal int Built => built.Count;
    internal int Reads { get; private set; }
    internal int CheckCalls { get; private set; }
    internal string? ProjectId => projectId;
    internal string? MessageText => message.IsVisible ? message.Text : null;

    private void Message(string text, bool failed = false)
    {
        message.Text = text;
        message.Foreground = failed ? Ui.Danger : Ui.Muted;
        message.IsVisible = true;
        scroll.IsVisible = false;
    }

    private async void Read(History? previous)
    {
        if (projectId is not { } project) return;
        if (previous is not null && reading) return;
        var ticket = previous is null ? ++generation : generation;
        reading = true;
        GitGraph page;
        try { page = await context.RequestAsync(BranchGraphContract.Graph, new GitGraphParams(project, previous?.Rows.Count ?? 0)); }
        catch (PluginCallException error)
        {
            if (ticket == generation) { reading = false; Message("Could not read the history.", failed: true); context.Log.Warn(error.Message); }
            return;
        }
        if (ticket != generation) return;
        reading = false;
        Reads++;
        var (rows, carry) = GraphLanes.Layout(page.Commits, previous?.Carry);
        history = new([.. previous?.Rows ?? [], .. rows], carry, previous?.Worktrees ?? page.Worktrees, page.HasMore);
        Show();
    }

    private void MaybePage()
    {
        if (history is not { HasMore: true } current || scroll.Viewport.Height <= 0) return;
        var remaining = scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height;
        if (remaining < LaneArt.RowHeight * Overscan) Read(current);
    }

    private void Show()
    {
        var rows = history!.Rows;
        if (rows.Count == 0) { Message("No commits on any branch yet."); return; }
        message.IsVisible = false;
        scroll.IsVisible = true;
        marks = [];
        foreach (var tree in history.Worktrees)
            (marks.TryGetValue(tree.Sha, out var names) ? names : marks[tree.Sha] = []).Add(tree.Name);
        drawnLanes = rows.Max(row => row.Width) + 1;
        body.Height = rows.Count * LaneArt.RowHeight;
        Window();
        scroll.UpdateLayout();
        MaybePage();
    }

    // Only the rows around the viewport exist; the scroller is sized by counting rows rather than building them.
    private void Window()
    {
        if (history is not { } current || current.Rows.Count == 0) return;
        var top = scroll.Offset.Y;
        var height = scroll.Viewport.Height;
        var first = Math.Max(0, (int)Math.Floor(top / LaneArt.RowHeight) - Overscan);
        var last = Math.Min(current.Rows.Count, (int)Math.Ceiling((top + height) / LaneArt.RowHeight) + Overscan);
        var previous = built.Values.ToDictionary(item => item.Model.Commit.Sha);
        // Every row on screen shares one gutter width, so the lanes stay in column while it changes.
        var shown = 1;
        for (var index = first; index < last; index++) shown = Math.Max(shown, current.Rows[index].Width + 1);
        shownLanes = shown;
        var next = new SortedDictionary<int, GraphCommitRow>();
        for (var index = first; index < last; index++)
        {
            var model = current.Rows[index];
            if (previous.TryGetValue(model.Commit.Sha, out var item))
            {
                item.Update(model, drawnLanes, shown, marks.GetValueOrDefault(model.Commit.Sha) ?? []);
                next[index] = item;
            }
            else next[index] = Row(model);
        }
        var controls = next.Values.ToArray();
        foreach (var control in window.Children.Where(control => !controls.Contains(control)).ToArray()) window.Children.Remove(control);
        for (var index = 0; index < controls.Length; index++)
        {
            var existing = window.Children.IndexOf(controls[index]);
            if (existing < 0) window.Children.Insert(index, controls[index]);
            else if (existing != index) window.Children.Move(existing, index);
        }
        built.Clear();
        foreach (var item in next) built.Add(item.Key, item.Value);
        foreach (var item in next.Values) item.SetChecks(checks.GetValueOrDefault(item.Model.Commit.Sha));
        if (next.Values.Any(item => !answered.Contains(item.Model.Commit.Sha) && !asking.Contains(item.Model.Commit.Sha))) { settle.Stop(); settle.Start(); }
        window.Margin = new Thickness(0, first * LaneArt.RowHeight, 0, 0);
    }

    private GraphCommitRow Row(GraphRow row)
    {
        var made = new GraphCommitRow(context, () => workspaceId, row, drawnLanes, shownLanes,
            marks.GetValueOrDefault(row.Commit.Sha) ?? [], sha => _ = CopyHashAsync(sha), sha => _ = CopyPatchAsync(sha), Open);
        made.SetChecks(checks.GetValueOrDefault(row.Commit.Sha));
        return made;
    }

    private void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && TopLevel.GetTopLevel(this)?.Launcher is { } launcher) _ = launcher.LaunchUriAsync(uri);
    }

    // Only the commits on screen are asked about, after the scroll settles, at most 50 a call. The host remembers answers, so asking again is cheap.
    private async Task AskChecksAsync(bool refresh)
    {
        if (projectId is not { } project || !IsEffectivelyVisible) { UpdatePoll(); return; }
        var shown = built.Values.Select(item => item.Model.Commit.Sha).ToList();
        var wanted = shown.Where(sha => !asking.Contains(sha) && (refresh ? checks.GetValueOrDefault(sha)?.State == "pending" : !answered.Contains(sha))).Take(50).ToList();
        if (wanted.Count == 0) { UpdatePoll(); return; }
        var ticket = generation;
        CheckCalls++;
        foreach (var sha in wanted) asking.Add(sha);
        GitGraphChecks found;
        try { found = await context.RequestAsync(BranchGraphContract.Checks, new GitGraphChecksParams(project, wanted)); }
        catch (PluginCallException error)
        {
            if (ticket != generation) return;
            foreach (var sha in wanted) { asking.Remove(sha); answered.Add(sha); }
            if (!checksFailed) { checksFailed = true; context.Log.Warn("The commit checks could not be read: " + error.Message); }
            return;
        }
        if (ticket != generation) return;
        foreach (var sha in wanted)
        {
            asking.Remove(sha);
            answered.Add(sha);
            checks[sha] = found.Commits.FirstOrDefault(item => item.Sha == sha);
        }
        foreach (var item in built.Values) item.SetChecks(checks.GetValueOrDefault(item.Model.Commit.Sha));
        UpdatePoll();
    }

    // Running checks are looked at again every 30 seconds while any is on screen; nothing polls otherwise.
    private void UpdatePoll()
    {
        if (built.Values.Any(item => checks.GetValueOrDefault(item.Model.Commit.Sha)?.State == "pending")) { if (!poll.IsEnabled) poll.Start(); }
        else poll.Stop();
    }

    private async Task<bool> CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        try { await clipboard.SetTextAsync(text); return true; }
        catch { return false; }
    }

    private async Task CopyHashAsync(string sha)
    {
        if (!await CopyAsync(sha)) context.Notify(PluginNotificationKind.Error, "Could not copy commit hash");
    }

    private async Task CopyPatchAsync(string sha)
    {
        if (projectId is not { } project) return;
        try
        {
            var result = await context.RequestAsync(BranchGraphContract.Patch, new GitPatchParams(project, sha));
            if (!await CopyAsync(result.Patch)) context.Notify(PluginNotificationKind.Error, "Could not copy patch to clipboard");
        }
        catch (PluginCallException error) { context.Notify(PluginNotificationKind.Error, "Could not copy patch", error.Message); }
    }
}