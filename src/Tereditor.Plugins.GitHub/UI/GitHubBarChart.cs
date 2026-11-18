using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// A bar chart drawn straight to the drawing context: bars side by side with no gap, dashed grid lines with their values at the
/// right edge, labels under every few bars and an optional rotated axis title. It shows the slice [Start, End) of its values.
/// </summary>
internal sealed class GitHubBarChart : Control
{
    private const double LabelGap = 4, LabelWidth = 40, Top = 8, Intervals = 4, LabelSpacing = 64;
    private static readonly Pen Dashed = new(Ui.BorderBrush, 1, new DashStyle([3, 3], 0));

    private IReadOnlyList<double> values = [];
    private int start, end;
    private Func<int, string> label = _ => "";

    /// <summary>The title drawn up the left edge; none when empty.</summary>
    public string AxisTitle { get; init; } = "";

    /// <summary>Whether labels under the bars are drawn.</summary>
    public bool ShowBottomLabels { get; init; } = true;

    public int Start => start;

    public int End => end;

    public void Set(IReadOnlyList<double> data, int from, int to, Func<int, string> bottomLabel)
    {
        values = data; start = Math.Clamp(from, 0, data.Count); end = Math.Clamp(to, start, data.Count); label = bottomLabel;
        InvalidateVisual();
    }

    public void SetRange(int from, int to)
    {
        start = Math.Clamp(from, 0, values.Count); end = Math.Clamp(to, start, values.Count);
        InvalidateVisual();
    }

    /// <summary>The grid maximum for a largest value: four equal steps of 1, 2, 2.5, 5 or 10 times a power of ten.</summary>
    internal static double NiceMax(double largest)
    {
        if (largest <= 0) return Intervals;
        var raw = largest / Intervals;
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var factor in new[] { 1, 2, 2.5, 5, 10 })
            if (factor * power >= raw) return factor * power * Intervals;
        return 10 * power * Intervals;
    }

    internal static string Format(double value) =>
        value >= 10000 ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : value.ToString("N0", CultureInfo.InvariantCulture);

    private static FormattedText Draw(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Typography.UiCaption.FontFamily), Typography.UiCaption.FontSize, brush);

    public override void Render(DrawingContext context)
    {
        var left = AxisTitle.Length > 0 ? 24 : 0;
        var bottom = ShowBottomLabels ? 24 : 8;
        var plot = new Rect(left, Top, Math.Max(0, Bounds.Width - left - LabelWidth - LabelGap), Math.Max(0, Bounds.Height - Top - bottom));
        if (plot.Width <= 0 || plot.Height <= 0) return;
        var count = end - start;
        var largest = 0.0;
        for (var i = start; i < end; i++) largest = Math.Max(largest, values[i]);
        var top = NiceMax(largest);
        for (var step = 0; step <= Intervals; step++)
        {
            var y = Math.Round(plot.Bottom - plot.Height * step / Intervals) + 0.5;
            if (step > 0) context.DrawLine(Dashed, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = Draw(Format(top * step / Intervals), Ui.Muted);
            context.DrawText(text, new Point(plot.Right + LabelGap, y - text.Height / 2));
        }
        context.DrawLine(new Pen(Ui.BorderBrush, 1), new Point(plot.Left, plot.Bottom + 0.5), new Point(plot.Right, plot.Bottom + 0.5));
        if (count <= 0) return;
        var width = plot.Width / count;
        for (var i = 0; i < count; i++)
        {
            var height = values[start + i] / top * plot.Height;
            if (values[start + i] > 0) height = Math.Max(height, 1);
            var x0 = plot.Left + i * width;
            context.DrawRectangle(Ui.Accent, null, new Rect(x0, plot.Bottom - height, Math.Max(width, 0.5), height));
        }
        if (ShowBottomLabels)
        {
            var every = Math.Max(1, (int)Math.Ceiling(LabelSpacing / width));
            for (var i = 0; i < count; i += every)
            {
                var text = Draw(label(start + i), Ui.Muted);
                var x = Math.Clamp(plot.Left + (i + 0.5) * width - text.Width / 2, plot.Left, Math.Max(plot.Left, plot.Right - text.Width));
                context.DrawText(text, new Point(x, plot.Bottom + 8));
            }
        }
        if (AxisTitle.Length > 0)
        {
            var title = Draw(AxisTitle, Ui.Muted);
            using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(0, plot.Top + (plot.Height + title.Width) / 2)))
                context.DrawText(title, new Point(0, 0));
        }
    }
}
