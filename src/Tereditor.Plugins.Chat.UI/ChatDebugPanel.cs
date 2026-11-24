using System.Collections.ObjectModel;
using System.Text;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Chat.UI;

/// <summary>One line of the debug log as the list holds it: the entry and whether it is open.</summary>
internal sealed class ChatDebugRow(ChatDebugEntry entry)
{
    public ChatDebugEntry Entry { get; } = entry;
    public bool Expanded { get; set; }
}

/// <summary>
/// The debug log of a chat: the raw lines exchanged with the agent process, in order, each with its direction, time, size and a one-line summary
/// that opens to the (credential-free) text. The list is virtualized; the log is bounded like the host's (<see cref="ChatDebugLimits"/>) and lives in memory only.
/// </summary>
internal sealed class ChatDebugPanel : Grid
{
    private const int ShownChars = 64 * 1024;

    private readonly string agent;
    private readonly List<ChatDebugEntry> all = [];
    private readonly ObservableCollection<ChatDebugRow> rows = [];
    private readonly ListBox list;
    private readonly TextBox filter;
    private readonly CheckBox streamEvents;
    private readonly TextBlock count = Ui.Text("", Ui.Muted, 11);
    private long chars;
    private long lastSeq;
    private bool changing;

    /// <summary>Puts text on the clipboard; replaced by a check.</summary>
    internal Func<string, Task> Copier { get; set; }

    /// <summary>Asked to empty the host's copy of the log too.</summary>
    internal event Action? ClearRequested;

    internal event Action? CloseRequested;

    internal ChatDebugPanel(string agent)
    {
        this.agent = agent;
        Copier = CopyToClipboard;
        Name = "ChatDebugPanel";
        Background = Ui.Elevated;
        RowDefinitions = new RowDefinitions("Auto,Auto,*");
        ZIndex = 10;
        IsVisible = false;

        var title = Ui.Text("Debug", Ui.TextBrush, 13);
        title.FontWeight = FontWeight.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        var note = Ui.Text("Credentials are hidden", Ui.Muted, 11);
        note.Name = "ChatDebugNote";
        note.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(note, "Keys such as token, apiKey, authorization, password and secret, and bearer values, are hidden in this log. The real traffic is untouched. The log stays in memory.");
        count.VerticalAlignment = VerticalAlignment.Center;
        var copyAll = Ui.Button("Copy all", () => _ = Copier(FormatAll()));
        copyAll.Name = "ChatDebugCopyAll";
        var clear = Ui.Button("Clear", () => { ClearLocal(); ClearRequested?.Invoke(); });
        clear.Name = "ChatDebugClear";
        var close = Ui.IconButton("close", "Close the debug log", () => CloseRequested?.Invoke());
        close.Name = "ChatDebugClose";
        var head = new DockPanel { Margin = new Thickness(12, 4, 4, 0) };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        DockPanel.SetDock(clear, Dock.Right);
        head.Children.Add(clear);
        DockPanel.SetDock(copyAll, Dock.Right);
        head.Children.Add(copyAll);
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { title, note, count } });
        SetRow(head, 0);
        Children.Add(head);

        filter = new TextBox { Name = "ChatDebugFilter", PlaceholderText = "Filter by type or summary", MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(filter, "Filter the debug log");
        filter.TextChanged += (_, _) => Refilter();
        streamEvents = new CheckBox { Name = "ChatDebugStreamEvents", Content = "Stream events", VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(streamEvents, "Show the stream_event lines, one per streamed token group. Hidden by default: they are most of the traffic.");
        streamEvents.IsCheckedChanged += (_, _) => Refilter();
        var bar = new DockPanel { Margin = new Thickness(12, 4, 12, 4) };
        DockPanel.SetDock(streamEvents, Dock.Right);
        bar.Children.Add(streamEvents);
        bar.Children.Add(filter);
        SetRow(bar, 1);
        Children.Add(bar);

        list = new ListBox { Name = "ChatDebugList", ItemsSource = rows, Background = Brushes.Transparent, ItemTemplate = new FuncDataTemplate<ChatDebugRow>((row, _) => row is null ? new Border() : Build(row)) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        SetRow(list, 2);
        Children.Add(list);
        ShowCount();
    }

    internal int Total => all.Count;

    /// <summary>The text the view holds, in characters.</summary>
    internal long Chars => chars;

    internal int Shown => rows.Count;

    internal IReadOnlyList<ChatDebugRow> Rows => rows;

    internal string Filter
    {
        get => filter.Text ?? "";
        set => filter.Text = value;
    }

    internal bool ShowStreamEvents
    {
        get => streamEvents.IsChecked == true;
        set => streamEvents.IsChecked = value;
    }

    internal ListBox List => list;

    private bool Matches(ChatDebugEntry entry)
    {
        if (entry.Type == "stream_event" && !ShowStreamEvents) return false;
        var query = Filter.Trim();
        return query.Length == 0 || entry.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Type.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Appends lines a push delivered; one already held (an older sequence number) is skipped.</summary>
    internal void Add(IEnumerable<ChatDebugEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Seq <= lastSeq) continue;
            lastSeq = entry.Seq;
            all.Add(entry);
            chars += entry.Text.Length;
            if (Matches(entry)) rows.Add(new ChatDebugRow(entry));
        }
        Trim();
        ShowCount();
    }

    /// <summary>Folds in what the host holds (older lines the view never saw), keeping the order and dropping duplicates.</summary>
    internal void Merge(IEnumerable<ChatDebugEntry> entries)
    {
        var merged = all.Concat(entries).GroupBy(entry => entry.Seq).Select(group => group.First()).OrderBy(entry => entry.Seq).ToList();
        all.Clear();
        all.AddRange(merged);
        lastSeq = all.Count > 0 ? all[^1].Seq : lastSeq;
        chars = all.Sum(entry => (long)entry.Text.Length);
        Trim();
        Refilter();
    }

    private void Trim()
    {
        var drop = 0;
        while (all.Count - drop > ChatDebugLimits.MaxEntries || chars > ChatDebugLimits.MaxChars) chars -= all[drop++].Text.Length;
        if (drop == 0) return;
        all.RemoveRange(0, drop);
        var first = all.Count > 0 ? all[0].Seq : long.MaxValue;
        while (rows.Count > 0 && rows[0].Entry.Seq < first) rows.RemoveAt(0);
    }

    internal void ClearLocal()
    {
        all.Clear();
        rows.Clear();
        chars = 0;
        ShowCount();
    }

    private void Refilter()
    {
        if (changing) return;
        var open = rows.Where(row => row.Expanded).Select(row => row.Entry.Seq).ToHashSet();
        rows.Clear();
        foreach (var entry in all.Where(Matches)) rows.Add(new ChatDebugRow(entry) { Expanded = open.Contains(entry.Seq) });
        ShowCount();
    }

    private void ShowCount() => count.Text = all.Count == rows.Count ? $"{all.Count} messages" : $"{rows.Count} of {all.Count} messages";

    /// <summary>The shown lines as text: a line of direction, time, size and summary, then the text, for each.</summary>
    internal string FormatAll() => Format(rows.Select(row => row.Entry), agent);

    internal static string Format(IEnumerable<ChatDebugEntry> entries, string agent)
    {
        var text = new StringBuilder();
        foreach (var entry in entries)
            text.Append(Heading(entry, agent)).Append('\n').Append(entry.Text).Append("\n\n");
        return text.ToString();
    }

    internal static string Heading(ChatDebugEntry entry, string agent) =>
        $"[{DateTimeOffset.FromUnixTimeMilliseconds(entry.TimeMs).ToLocalTime():HH:mm:ss.fff}] {Direction(entry.Direction, agent)} {Bytes(entry.Size)} {entry.Summary}";

    internal static string Direction(ChatDebugDirection direction, string agent) => direction switch
    {
        ChatDebugDirection.ToAgent => "to " + agent,
        ChatDebugDirection.FromAgent => "from " + agent,
        _ => "stderr"
    };

    private static string Bytes(int size) => size < 1024 ? size + " B" : size < 1024 * 1024 ? (size / 1024.0).ToString("0.#") + " KB" : (size / 1048576.0).ToString("0.#") + " MB";

    private Control Build(ChatDebugRow row)
    {
        var entry = row.Entry;
        var marker = Ui.Text(Direction(entry.Direction, agent), entry.Direction switch { ChatDebugDirection.Stderr => Ui.Danger, ChatDebugDirection.ToAgent => Ui.TextBrush, _ => Ui.Muted }, 11);
        marker.Width = 96;
        marker.FontWeight = FontWeight.SemiBold;
        marker.TextTrimming = TextTrimming.CharacterEllipsis;
        var time = Ui.Text($"{DateTimeOffset.FromUnixTimeMilliseconds(entry.TimeMs).ToLocalTime():HH:mm:ss.fff}", Ui.Muted, 11);
        time.FontFamily = Ui.CodeFont;
        var size = Ui.Text(Bytes(entry.Size), Ui.Muted, 11);
        size.Width = 56;
        size.TextAlignment = TextAlignment.Right;
        var summary = Ui.Text(entry.Summary, Ui.TextBrush, 12);
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        summary.VerticalAlignment = VerticalAlignment.Center;
        var copy = Ui.Button("Copy", () => _ = Copier(entry.Text));
        copy.Name = "ChatDebugCopy";
        copy.Padding = new Thickness(8, 0);
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), ColumnSpacing = 8, Background = Brushes.Transparent };
        foreach (var (control, column) in new (Control, int)[] { (marker, 0), (time, 1), (size, 2), (summary, 3), (copy, 4) })
        {
            SetColumn(control, column);
            line.Children.Add(control);
        }
        var stack = new StackPanel { Margin = new Thickness(4, 2) };
        stack.Children.Add(line);
        TextBox? detail = null;
        void Show()
        {
            if (row.Expanded && detail is null)
            {
                detail = new TextBox
                {
                    Name = "ChatDebugText",
                    Text = entry.Text.Length > ShownChars ? entry.Text[..ShownChars] + $"\n... {entry.Text.Length - ShownChars:N0} more characters; Copy has them all." : entry.Text,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = Ui.CodeFont,
                    FontSize = 11,
                    MaxHeight = 320,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(detail, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                ScrollViewer.SetVerticalScrollBarVisibility(detail, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                stack.Children.Add(detail);
            }
            else if (!row.Expanded && detail is not null)
            {
                stack.Children.Remove(detail);
                detail = null;
            }
        }
        line.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(line).Properties.IsLeftButtonPressed is false || e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;
            row.Expanded = !row.Expanded;
            Show();
        };
        Show();
        return stack;
    }

    /// <summary>Expands or folds a row, as a click on it does; for a check.</summary>
    internal void Toggle(ChatDebugRow row)
    {
        row.Expanded = !row.Expanded;
        changing = true;
        var at = rows.IndexOf(row);
        if (at >= 0) { rows.RemoveAt(at); rows.Insert(at, row); }
        changing = false;
    }

    private async Task CopyToClipboard(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
    }
}