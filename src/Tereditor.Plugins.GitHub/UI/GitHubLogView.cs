using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>Which steps and log groups of a run page are open, keyed by run, job and step; kept by the page, never by controls, so a redraw or a poll loses nothing.</summary>
internal sealed class GitHubRunState
{
    private readonly Dictionary<string, bool> steps = [];
    private readonly Dictionary<string, bool> groups = [];

    public static string StepKey(string run, string job, int step) => run + "|" + job + "|" + step.ToString(CultureInfo.InvariantCulture);

    public bool StepOpen(string key, bool failed) => steps.TryGetValue(key, out var open) ? open : failed;

    public void SetStep(string key, bool open) => steps[key] = open;

    public bool GroupOpen(string key, bool hasError) => groups.TryGetValue(key, out var open) ? open : hasError;

    public void SetGroup(string key, bool open) => groups[key] = open;
}

/// <summary>One visible line of a step's log: its number in the step, its place in the step's lines and the line itself.</summary>
internal sealed record GitHubLogRow(int Number, int Index, GitHubLogLine Line, bool GroupOpen);

/// <summary>
/// One step's log as a virtualized list: the lines stay plain strings in the data, and only the rows in view exist as controls,
/// each with its colour codes parsed when it is built. Groups (##[group]) collapse; the list is a fixed-height viewport so it
/// can virtualize inside the page's own scroll.
/// </summary>
internal sealed class GitHubLogView : Border
{
    internal const double LineHeight = 16;
    private const double ViewportHeight = 480;
    private const char Esc = '\x1b';

    private readonly GitHubLogStep step;
    private readonly string key;
    private readonly GitHubRunState state;
    private readonly ItemsControl items;
    private readonly ScrollViewer scroll;

    public GitHubLogView(GitHubLogStep step, string key, GitHubRunState state)
    {
        this.step = step;
        this.key = key;
        this.state = state;
        Name = "GitHubStepLog";
        items = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<GitHubLogRow>((row, _) => row is null ? null : Row(row))
        };
        scroll = new ScrollViewer { Content = items, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        Child = scroll;
        Margin = new Thickness(0, 4);
        Fill();
    }

    /// <summary>The rows that show: lines of a collapsed group are left out.</summary>
    public IReadOnlyList<GitHubLogRow> Rows => (IReadOnlyList<GitHubLogRow>)items.ItemsSource!;

    internal ScrollViewer Scroller => scroll;

    internal static List<GitHubLogRow> Visible(GitHubLogStep step, string key, GitHubRunState state)
    {
        var rows = new List<GitHubLogRow>();
        var lines = step.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.Header) { rows.Add(new(i + 1, i, line, true)); continue; }
            var end = i + 1;
            var error = false;
            while (end < lines.Count && lines[end].Grouped) { error |= lines[end].Error; end++; }
            var open = state.GroupOpen(key + "|g" + i.ToString(CultureInfo.InvariantCulture), error);
            rows.Add(new(i + 1, i, line, open));
            if (!open) i = end - 1;
        }
        return rows;
    }

    private void Fill()
    {
        var rows = Visible(step, key, state);
        items.ItemsSource = rows;
        Height = Math.Max(LineHeight, Math.Min(rows.Count * LineHeight, ViewportHeight)) + 8;
    }

    private Control Row(GitHubLogRow row)
    {
        var block = new TextBlock { FontFamily = Ui.CodeFont, FontSize = 12, Height = LineHeight, TextWrapping = TextWrapping.NoWrap, Foreground = Ui.TextBrush };
        block.Inlines!.Add(new Run(row.Number.ToString(CultureInfo.InvariantCulture).PadLeft(5) + "  ") { Foreground = Ui.Hint });
        if (row.Line.Time.Length > 0) block.Inlines.Add(new Run(row.Line.Time + "  ") { Foreground = Ui.Hint, FontStyle = FontStyle.Italic });
        if (row.Line.Header)
        {
            block.Inlines.Add(new Run((row.GroupOpen ? "▾ " : "▸ ") + Plain(row.Line.Text)) { FontWeight = FontWeight.SemiBold });
            block.Cursor = new Cursor(StandardCursorType.Hand);
            block.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(block).Properties.IsLeftButtonPressed) Toggle(row.Index, !row.GroupOpen);
            };
            return block;
        }
        foreach (var run in Runs(row.Line.Text, row.Line.Error ? Ui.Danger : Ui.TextBrush)) block.Inlines.Add(run);
        if (row.Line.Grouped) block.Margin = new Thickness(16, 0, 0, 0);
        return block;
    }

    /// <summary>Opens or closes the group whose header is line <paramref name="index"/> of the step.</summary>
    internal void Toggle(int index, bool open)
    {
        state.SetGroup(key + "|g" + index.ToString(CultureInfo.InvariantCulture), open);
        var offset = scroll.Offset;
        Fill();
        scroll.Offset = offset;
    }

    internal static string Plain(string text) => text.IndexOf(Esc) < 0 ? text : string.Concat(Runs(text, null).Select(run => run.Text));

    /// <summary>Splits a line at its colour codes (SGR) into runs; colours come from the theme's roles, never raw values.</summary>
    internal static IEnumerable<Run> Runs(string text, IBrush? brush)
    {
        if (text.IndexOf(Esc) < 0) { yield return new Run(text) { Foreground = brush }; yield break; }
        var colour = brush;
        var bold = false;
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != Esc || i + 1 >= text.Length || text[i + 1] != '[') { i++; continue; }
            var end = i + 2;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] is ';' or '?')) end++;
            var final = end < text.Length ? text[end] : ' ';
            if (i > start) yield return new Run(text[start..i]) { Foreground = colour, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
            if (final == 'm')
            {
                var codes = text[(i + 2)..end].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(c => int.TryParse(c, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
                if (codes.Length == 0) codes = [0];
                for (var k = 0; k < codes.Length; k++)
                {
                    var code = codes[k];
                    if (code == 0) { colour = brush; bold = false; }
                    else if (code == 1) bold = true;
                    else if (code == 22) bold = false;
                    else if (code == 39) colour = brush;
                    else if (code is 38 or 48) k += k + 1 < codes.Length && codes[k + 1] == 2 ? 4 : 2;
                    else if (code is >= 30 and <= 37 or >= 90 and <= 97) colour = Tone(code % 10, code >= 90) ?? brush;
                }
            }
            i = Math.Min(end + 1, text.Length);
            start = i;
        }
        if (start < text.Length) yield return new Run(text[start..]) { Foreground = colour, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
    }

    private static IBrush? Tone(int index, bool bright) => index switch
    {
        0 => bright ? Ui.Muted : Ui.Hint,
        1 => Ui.Danger,
        2 => Ui.Success,
        3 => Ui.Warning,
        4 or 6 => Ui.Info,
        5 => Ui.Accent,
        7 => Ui.TextBrush,
        _ => null
    };
}