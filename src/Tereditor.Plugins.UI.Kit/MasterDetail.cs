using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>
/// A list on the left and the selected item's settings on the right, as on Settings › Code Intelligence: the list has a divider at its
/// right edge that the user can drag, a highlighted selected row and a small red dot on a row with a problem. Plugins fill the list with
/// <see cref="Row"/> items, optionally under <see cref="Group"/> headings (which cannot be selected), and put the selected item's pane in
/// <see cref="Detail"/>.
/// </summary>
public sealed class MasterDetail : Grid
{
    private bool setting;
    private object? previous;

    private sealed class GroupHeading : TextBlock;

    public MasterDetail(string name, double railWidth = 280)
    {
        Name = name;
        ColumnDefinitions = new ColumnDefinitions { new(new GridLength(railWidth)) { MinWidth = 160 }, new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)) };
        Rail = new ListBox { Name = name + "Rail", Background = Brushes.Transparent, BorderThickness = new Thickness(0), AutoScrollToSelectedItem = false };
        ScrollViewer.SetVerticalScrollBarVisibility(Rail, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(Rail, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Rail.SelectionChanged += (_, _) =>
        {
            if (setting) return;
            if (Rail.SelectedItem is GroupHeading)
            {
                // A heading is not an item: put the selection back.
                setting = true;
                try { Rail.SelectedItem = previous; }
                finally { setting = false; }
                return;
            }
            previous = Rail.SelectedItem;
            Selected?.Invoke(SelectedId);
        };
        var divider = new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 8, 0), Child = RailColumn() };
        Detail = new ContentControl { Name = name + "Detail", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var detailScroll = new ScrollViewer { Name = name + "DetailScroll", HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = Detail };
        var splitter = new GridSplitter { Name = name + "Splitter", Width = 5, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        SetColumn(splitter, 1);
        SetColumn(detailScroll, 2);
        Children.Add(divider);
        Children.Add(splitter);
        Children.Add(detailScroll);
    }

    /// <summary>Header and Footer keep their size and the list takes the rest and scrolls on its own, so the page around it does not move.</summary>
    private Grid RailColumn()
    {
        var column = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        Grid.SetRow(Rail, 1);
        Grid.SetRow(Footer, 2);
        column.Children.Add(Header);
        column.Children.Add(Rail);
        column.Children.Add(Footer);
        return column;
    }

    public ListBox Rail { get; }

    /// <summary>The width of the list in pixels. The user can drag it; at least 160.</summary>
    public double RailWidth
    {
        get => ColumnDefinitions[0].Width.Value;
        set => ColumnDefinitions[0].Width = new GridLength(Math.Max(value, 160));
    }

    /// <summary>Above the list: actions that are not items of it, and a heading for it.</summary>
    public StackPanel Header { get; } = new() { Spacing = 4 };

    /// <summary>Under the list: buttons that act on the list itself, such as adding an item.</summary>
    public StackPanel Footer { get; } = new() { Spacing = 4 };

    /// <summary>The right-hand pane: whatever the selected row configures.</summary>
    public ContentControl Detail { get; }

    /// <summary>Raised with the id of the row the user selected (not when the list is filled by <see cref="SetRows"/>).</summary>
    public event Action<string?>? Selected;

    public string? SelectedId => (Rail.SelectedItem as Control)?.Tag as string;

    /// <summary>Replaces the rows and keeps <paramref name="select"/> selected (the first row when it is absent, none when <paramref name="none"/>). Does not raise <see cref="Selected"/>.</summary>
    public void SetRows(IReadOnlyList<Control> rows, string? select, bool none = false)
    {
        setting = true;
        try
        {
            Rail.ItemsSource = rows;
            var items = rows.Where(row => row is not GroupHeading).ToList();
            previous = none ? null : items.FirstOrDefault(row => row.Tag as string == select) ?? items.FirstOrDefault();
            Rail.SelectedItem = previous;
        }
        finally { setting = false; }
    }

    /// <summary>A heading between groups of rows, such as Installed and Registry. It is not selectable.</summary>
    public static Control Group(string id, string text) =>
        new GroupHeading { Name = "MasterGroup_" + id, Text = text, FontSize = 12, Foreground = Ui.Muted, Margin = new Thickness(2, 8, 0, 0) };

    /// <summary>One row of the list: an icon, the name with a small line under it, and a red dot (with the problem as its tooltip) when something is wrong.</summary>
    public static Control Row(string id, Control? icon, string name, string? subtitle, string? problem) => Row(id, icon, name, null, subtitle, problem, null);

    /// <summary>
    /// A row with more: <paramref name="note"/> sits after the name in small muted text (a version), <paramref name="description"/> is one trimmed line under
    /// it, and <paramref name="action"/> (an Install button, a check mark) sits at the right edge before the problem dot. The name trims with an ellipsis; the note and the optional
    /// <paramref name="badge"/> (a small muted tag, <paramref name="badgeTip"/> says why) keep their width after it, so nothing overlaps.
    /// </summary>
    public static Control Row(string id, Control? icon, string name, string? note, string? description, string? problem, Control? action, string? badge = null, string? badgeTip = null)
    {
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions { new(GridLength.Star) { MinWidth = 48 }, new(GridLength.Auto), new(GridLength.Auto) } };
        title.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.TextBrush, HorizontalAlignment = HorizontalAlignment.Left });
        if (!string.IsNullOrEmpty(note))
        {
            var version = new TextBlock { Name = "MasterNote_" + id, Text = note, FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            SetColumn(version, 1);
            title.Children.Add(version);
        }
        if (!string.IsNullOrEmpty(badge))
        {
            var tag = new TextBlock { Name = "MasterBadge_" + id, Text = badge, FontSize = 11, Foreground = Ui.Muted, FontStyle = FontStyle.Italic, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            if (badgeTip is not null) ToolTip.SetTip(tag, badgeTip);
            SetColumn(tag, 2);
            title.Children.Add(tag);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        if (!string.IsNullOrEmpty(description)) text.Children.Add(new TextBlock { Text = description, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Muted });
        var row = new Grid { Tag = id, Name = "MasterRow_" + id, ColumnDefinitions = new ColumnDefinitions { new(new GridLength(24)), new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto), new(GridLength.Auto) } };
        if (icon is not null) { icon.VerticalAlignment = VerticalAlignment.Center; icon.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(icon); }
        SetColumn(text, 1);
        row.Children.Add(text);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            action.Margin = new Thickness(4, 0, 0, 0);
            SetColumn(action, 2);
            row.Children.Add(action);
        }
        if (problem is not null)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { Name = "MasterProblem_" + id, Width = 6, Height = 6, Fill = Ui.Danger, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            ToolTip.SetTip(dot, problem);
            SetColumn(dot, 3);
            row.Children.Add(dot);
        }
        return row;
    }
}