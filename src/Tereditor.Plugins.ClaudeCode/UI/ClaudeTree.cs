using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.ClaudeCode.UI;

/// <summary>
/// The tree lines of the pane: an <c>@</c>-import hangs under the file that pulled it in, and a plugin hangs under the
/// marketplace it came from. A row at depth N gets a rule per ancestor level, then a tee or an elbow into the row; the
/// parent's glyph carries the stem the first child's rule continues from.
/// </summary>
internal static class ClaudeTree
{
    /// <summary>The guides to the left of a row at <paramref name="depth"/>; <paramref name="last"/> is true for the last child of its parent, which ends in an elbow instead of a tee.</summary>
    public static IEnumerable<Control> Guides(int depth, bool last)
    {
        for (var level = 0; level < depth; level++)
        {
            var guide = new Grid { Width = 5, Margin = new Thickness(15, 0, 0, 0), RowDefinitions = new RowDefinitions("*,*") };
            if (level == depth - 1)
            {
                guide.Name = "ClaudeContextConnector";
                guide.Tag = last ? "last" : "tee";
                var rule = new Border { Width = 1, Background = Ui.BorderBrush, HorizontalAlignment = HorizontalAlignment.Left };
                if (!last) Grid.SetRowSpan(rule, 2);
                guide.Children.Add(rule);
                var arm = new Border { Height = 1, Background = Ui.BorderBrush, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -8, 0) };
                guide.Children.Add(arm);
            }
            else guide.Children.Add(new Border { Width = 1, Background = Ui.BorderBrush, HorizontalAlignment = HorizontalAlignment.Left, [Grid.RowSpanProperty] = 2 });
            DockPanel.SetDock(guide, Dock.Left);
            yield return guide;
        }
    }

    /// <summary>A parent's glyph: <paramref name="icon"/> over two rows, with the stem under it when the row has children to hang from it.</summary>
    public static Grid Glyph(Control icon, bool hasChildren)
    {
        icon.VerticalAlignment = VerticalAlignment.Center;
        var glyph = new Grid { Margin = new Thickness(0, -4, 8, -4), RowDefinitions = new RowDefinitions("*,*") };
        Grid.SetRowSpan(icon, 2);
        glyph.Children.Add(icon);
        if (hasChildren)
            glyph.Children.Add(new Border
            {
                Name = "ClaudeContextStem",
                Width = 1,
                Background = Ui.BorderBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(7, 7, 0, 0),
                [Grid.RowProperty] = 1
            });
        DockPanel.SetDock(glyph, Dock.Left);
        return glyph;
    }

    private static Geometry ChevronShape(bool open) => Geometry.Parse(open ? "M 3,5 L 7,9 L 11,5" : "M 5,3 L 9,7 L 5,11");

    /// <summary>The chevron after a parent's name: down while its children show, right while they are hidden.</summary>
    public static Avalonia.Controls.Shapes.Path Chevron(bool open) => new()
    {
        Name = "ClaudeCapabilityChevron",
        Data = ChevronShape(open),
        Width = 14,
        Height = 14,
        Stroke = Ui.Muted,
        StrokeThickness = 1.5,
        Margin = new Thickness(4, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    public static void SetChevron(Avalonia.Controls.Shapes.Path chevron, bool open) => chevron.Data = ChevronShape(open);
}