using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// One workflow run (<c>run:ID</c>): jobs on the left, the selected job's steps on the right. A step opens to its log, a virtualized
/// line list. What is open lives in <see cref="GitHubRunState"/>, keyed by run, job and step, so a poll or a redraw keeps it.
/// </summary>
internal sealed class GitHubRunTab : Border
{
    private readonly IPluginUIContext? context;
    private readonly string workspace;
    private readonly string runId;
    private readonly Func<GitHubViewParams, CancellationToken, Task<GitHubView>> loadView;
    private readonly Func<string, CancellationToken, Task<GitHubLog>> loadLog;
    private readonly Func<DateTimeOffset> now;
    private readonly GitHubRunState state = new();
    private readonly Dictionary<string, GitHubLog> logs = [];
    private readonly HashSet<string> loading = [];
    private readonly DispatcherTimer poll = new();
    private readonly TextBlock title = Ui.Text("", Ui.TextBrush, 20);
    private readonly TextBlock subtitle = Ui.Text("", Ui.Muted, 13);
    private readonly TextBlock status = Ui.Text("", Ui.Muted, 14);
    private readonly StackPanel jobsPanel = new() { Name = "GitHubJobs" };
    private readonly StackPanel stepsPanel = new() { Name = "GitHubSteps" };
    private readonly TextBlock jobTitle = Ui.Text("", Ui.TextBrush, 16);
    private readonly Border content = new();
    private GitHubView? shown;
    private string? revision;
    private string selectedJob = "";
    private int generation;
    private CancellationTokenSource? lifetime;
    private readonly GitHubFreshness freshness;

    public GitHubRunTab(IPluginUIContext? context, string workspace, string id, Func<GitHubViewParams, CancellationToken, Task<GitHubView>>? loadView = null,
        Func<string, CancellationToken, Task<GitHubLog>>? loadLog = null, Func<DateTimeOffset>? now = null)
    {
        this.context = context;
        this.workspace = workspace;
        runId = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.loadView = loadView ?? ((request, token) => context is null ? Task.FromException<GitHubView>(new InvalidOperationException("No host.")) : context.RequestAsync(GitHubContract.View, request, token).AsTask());
        this.loadLog = loadLog ?? ((job, token) => context is null ? Task.FromException<GitHubLog>(new InvalidOperationException("No host.")) : context.RequestAsync(GitHubContract.Log, new GitHubLogParams(workspace, job), token).AsTask());
        Name = "GitHubRunTab";
        title.FontWeight = FontWeight.SemiBold; title.TextWrapping = TextWrapping.Wrap;
        subtitle.TextWrapping = TextWrapping.Wrap; subtitle.TextTrimming = TextTrimming.None;
        var top = new StackPanel { Spacing = 4, Margin = new Thickness(24, 16, 24, 12), Children = { title, subtitle, status } };
        var left = new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 1, 0), Child = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(8), Children = { jobsPanel } } } };
        var bar = new Grid { ColumnDefinitions = new("*,Auto,Auto"), Margin = new Thickness(16, 8) };
        bar.Children.Add(jobTitle);
        var expand = Ui.Button("Expand all", () => SetAll(true)); expand.Name = "GitHubExpandAll";
        var collapse = Ui.Button("Collapse all", () => SetAll(false)); collapse.Name = "GitHubCollapseAll";
        collapse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(expand, 1); Grid.SetColumn(collapse, 2);
        bar.Children.Add(expand); bar.Children.Add(collapse);
        var right = new Grid { RowDefinitions = new("Auto,*") };
        right.Children.Add(bar);
        var steps = new ScrollViewer { Content = new Border { Margin = new Thickness(16, 0), Child = stepsPanel }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(steps, 1); right.Children.Add(steps);
        var body = new Grid { ColumnDefinitions = new("260,*"), Margin = new Thickness(0) };
        body.Children.Add(left); Grid.SetColumn(right, 1); body.Children.Add(right);
        var root = new Grid { RowDefinitions = new("Auto,*") };
        root.Children.Add(top); Grid.SetRow(body, 1); root.Children.Add(body);
        content.Child = root;
        freshness = new(content, Refresh);
        Child = freshness;
        poll.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            lifetime = new();
            poll.Interval = TimeSpan.FromSeconds(Math.Clamp(context?.Settings<GitHubSettings>().PollSeconds ?? 60, 15, 600));
            poll.Start(); Refresh();
        };
        DetachedFromVisualTree += (_, _) => { poll.Stop(); lifetime?.Cancel(); generation++; };
    }

    internal string SelectedJob => selectedJob;

    private void Refresh() { if (lifetime is not null) _ = LoadAsync(); }

    /// <summary>Asks for the run once and shows it; a check calls it with its own loader.</summary>
    internal async Task LoadAsync()
    {
        var mine = ++generation;
        var token = lifetime?.Token ?? CancellationToken.None;
        GitHubView view;
        try { view = await Task.Run(() => loadView(new GitHubViewParams(workspace, "runs", runId, [], 30, revision), token), token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { if (mine == generation) Show(error.Message); return; }
        if (mine != generation || token.IsCancellationRequested) return;
        freshness.Apply(view.Cached);
        if (view.Problem is not null) { Show(view.Problem); return; }
        if (view.Unchanged) return;
        revision = view.Revision;
        Apply(view);
    }

    private void Show(string message)
    {
        stepsPanel.Children.Clear();
        var line = Ui.Text(message, Ui.Muted, 14); line.TextWrapping = TextWrapping.Wrap; line.TextTrimming = TextTrimming.None;
        stepsPanel.Children.Add(line);
    }

    /// <summary>The jobs of the run, each with the step rows that follow it.</summary>
    private List<(GitHubRow Job, List<GitHubRow> Steps)> Jobs()
    {
        var jobs = new List<(GitHubRow, List<GitHubRow>)>();
        foreach (var row in shown?.Rows ?? [])
        {
            if (row.Indent == 0) jobs.Add((row, []));
            else if (jobs.Count > 0) jobs[^1].Item2.Add(row);
        }
        return jobs;
    }

    internal void Apply(GitHubView view)
    {
        shown = view;
        freshness.Url = GitHubWebLink.RepoUrl(view) is { Length: > 0 } repoUrl ? repoUrl + "/actions/runs/" + runId : "";
        title.Text = view.Title;
        subtitle.Text = view.Subtitle;
        status.Text = GitHubRuns.StateText(view.State);
        status.Foreground = GitHubRuns.Tone(view.State);
        var jobs = Jobs();
        foreach (var job in jobs)
            if (logs.TryGetValue(job.Job.Id, out var old) && old.InProgress && job.Job.State is not ("running" or "queued")) logs.Remove(job.Job.Id);
        if (!jobs.Any(job => job.Job.Id == selectedJob)) selectedJob = (jobs.FirstOrDefault(job => job.Job.State == "failure").Job ?? jobs.FirstOrDefault().Job)?.Id ?? "";
        Render();
    }

    private void Render()
    {
        var jobs = Jobs();
        jobsPanel.Children.Clear();
        stepsPanel.Children.Clear();
        foreach (var (job, _) in jobs) jobsPanel.Children.Add(JobRow(job));
        var current = jobs.FirstOrDefault(job => job.Job.Id == selectedJob);
        if (current.Job is null) { jobTitle.Text = ""; if (jobs.Count == 0) stepsPanel.Children.Add(Ui.Text("This run has no jobs yet.", Ui.Muted, 14)); return; }
        jobTitle.Text = current.Job.Title;
        var anyOpen = current.Steps.Any(step => state.StepOpen(Key(current.Job, step), step.State == "failure"));
        if (anyOpen && !logs.ContainsKey(current.Job.Id) && current.Job.State is not ("running" or "queued")) _ = EnsureLogAsync(current.Job.Id);
        foreach (var step in current.Steps) stepsPanel.Children.Add(StepBlock(current.Job, step));
        if (current.Job.Url.StartsWith("https://github.com/", StringComparison.Ordinal))
        {
            var open = Ui.Button("Open full log on GitHub", () => GitHubRuns.Open(current.Job.Url), GitHubIcons.ExternalLink);
            open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(0, 8);
            stepsPanel.Children.Add(open);
        }
    }

    private string Key(GitHubRow job, GitHubRow step) => GitHubRunState.StepKey(runId, job.Id, StepNumber(step));

    private static int StepNumber(GitHubRow step) => int.TryParse(step.Id[(step.Id.LastIndexOf('/') + 1)..], out var number) ? number : 0;

    private Control JobRow(GitHubRow job)
    {
        var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(8, 8) };
        var icon = Ui.Icon(GitHubRuns.Glyph(job.State), GitHubRuns.Tone(job.State), 16);
        row.Children.Add(icon);
        var name = Ui.Text(job.Title, Ui.TextBrush, 14); name.Margin = new Thickness(8, 0);
        Grid.SetColumn(name, 1); row.Children.Add(name);
        if (job.Right.Length > 0) { var time = Ui.Text(job.Right, Ui.Muted, 12); Grid.SetColumn(time, 2); row.Children.Add(time); }
        var border = new Border
        {
            Name = "GitHubJob" + job.Id,
            Child = row,
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = job.Id == selectedJob ? Ui.Selected : Brushes.Transparent
        };
        border.PointerPressed += (_, e) => { if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) Select(job.Id); };
        return border;
    }

    /// <summary>Shows the steps of a job.</summary>
    internal void Select(string jobId) { selectedJob = jobId; Render(); }

    private Control StepBlock(GitHubRow job, GitHubRow step)
    {
        var key = Key(job, step);
        var open = state.StepOpen(key, step.State == "failure");
        var line = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(8, 8) };
        line.Children.Add(Ui.Icon(open ? GitHubIcons.ArrowDown : GitHubIcons.ArrowRight, Ui.Muted, 16));
        var icon = Ui.Icon(GitHubRuns.Glyph(step.State), GitHubRuns.Tone(step.State), 16); icon.Margin = new Thickness(8, 0);
        Grid.SetColumn(icon, 1); line.Children.Add(icon);
        var name = Ui.Text(step.Title, Ui.TextBrush, 14); Grid.SetColumn(name, 2); line.Children.Add(name);
        var time = step.Right.Length > 0 ? step.Right : step.State == "running" ? GitHubRuns.Elapsed(step.At, now()) : "";
        if (time.Length > 0) { var duration = Ui.Text(time, Ui.Muted, 12); Grid.SetColumn(duration, 3); line.Children.Add(duration); }
        var header = new Border { Name = "GitHubStep" + step.Id.Replace('/', '_'), Child = line, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        header.PointerPressed += (_, e) => { if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) ToggleStep(job.Id, StepNumber(step)); };
        var block = new StackPanel { Name = "GitHubStepBlock" + step.Id.Replace('/', '_') };
        block.Children.Add(header);
        if (open) block.Children.Add(StepLog(job, step, key));
        return new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = block };
    }

    private Control StepLog(GitHubRow job, GitHubRow step, string key)
    {
        var margin = new Thickness(40, 0, 0, 8);
        Control Note(string text) { var note = Ui.Text(text, Ui.Muted, 13); note.Margin = margin; note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None; return note; }
        if (!logs.TryGetValue(job.Id, out var log)) return Note(job.State is "running" or "queued" ? NotReady(step) : "Loading the log…");
        if (log.InProgress) return Note(NotReady(step));
        if (log.Problem is not null) return Note(log.Problem);
        var found = log.Steps.FirstOrDefault(s => s.Number == StepNumber(step)) ?? (log.Steps.Count == 1 && log.Steps[0].Number == 0 ? log.Steps[0] : null);
        if (found is null || found.Lines.Count == 0) return Note(step.State is "skipped" ? "This step was skipped." : "This step wrote no log lines.");
        var view = new GitHubLogView(found, key, state) { Margin = margin };
        if (found.Cut > 0)
        {
            var panel = new StackPanel { Children = { Note(found.Cut + " earlier lines were left out to keep this short."), view } };
            return panel;
        }
        return view;
    }

    private string NotReady(GitHubRow step)
    {
        var elapsed = step.State == "running" ? GitHubRuns.Elapsed(step.At, now()) : step.Right;
        return "Not ready yet. The log is available when the job finishes." + (elapsed.Length > 0 ? " Step time so far: " + elapsed + "." : "");
    }

    /// <summary>Opens or closes one step; the choice is kept by key across redraws and polls.</summary>
    internal void ToggleStep(string jobId, int number)
    {
        var job = Jobs().FirstOrDefault(j => j.Job.Id == jobId);
        var step = job.Steps?.FirstOrDefault(s => StepNumber(s) == number);
        if (step is null) return;
        var key = Key(job.Job, step);
        state.SetStep(key, !state.StepOpen(key, step.State == "failure"));
        selectedJob = jobId;
        Render();
    }

    /// <summary>Expand all and Collapse all: every step of the selected job.</summary>
    internal void SetAll(bool open)
    {
        var job = Jobs().FirstOrDefault(j => j.Job.Id == selectedJob);
        if (job.Job is null) return;
        foreach (var step in job.Steps) state.SetStep(Key(job.Job, step), open);
        Render();
    }

    internal void ApplyLog(string jobId, GitHubLog log) { logs[jobId] = log; Render(); }

    private async Task EnsureLogAsync(string jobId)
    {
        if (!loading.Add(jobId)) return;
        var token = lifetime?.Token ?? CancellationToken.None;
        GitHubLog log;
        try { log = await Task.Run(() => loadLog(jobId, token), token); }
        catch (OperationCanceledException) { loading.Remove(jobId); return; }
        catch (Exception error) { log = new([], 0, "", error.Message); }
        loading.Remove(jobId);
        if (token.IsCancellationRequested) return;
        logs[jobId] = log;
        Render();
    }

    /// <summary>A check's way to wait for the log a step asked for.</summary>
    internal async Task SettleAsync() { while (loading.Count > 0) await Task.Delay(10); }
}