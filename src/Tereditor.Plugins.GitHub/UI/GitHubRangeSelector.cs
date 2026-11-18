using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// A small area chart of the whole history with a window over it: drag either handle to move one edge, the window to move both,
/// or click outside it to centre it there. A thin track below shows the window like a scroll bar.
/// </summary>
internal sealed class GitHubRangeSelector : Control
{
    private const double Plot = 40, Handle = 8, Track = 8, Gap = 4, MinWeeks = 4;
    private IReadOnlyList<double> values = [];
    private int start, end;
    private int dragging;
    private double anchor;
    private int anchorStart, anchorEnd;

    public GitHubRangeSelector() { Height = Plot + Gap + Track; Cursor = new Cursor(StandardCursorType.Hand); }

    /// <summary>Called with the window while dragging, and again with final true when the pointer is released.</summary>
    public event Action<int, int, bool>? RangeChanged;

    public void Set(IReadOnlyList<double> data, int from, int to)
    {
        values = data; start = from; end = to;
        InvalidateVisual();
    }

    public void SetRange(int from, int to) { start = from; end = to; InvalidateVisual(); }

    private double Step => values.Count == 0 ? 0 : Bounds.Width / values.Count;

    public override void Render(DrawingContext context)
    {
        var count = values.Count;
        if (count == 0 || Bounds.Width <= 0) return;
        var largest = Math.Max(1, values.Max());
        var area = new StreamGeometry();
        using (var path = area.Open())
        {
            path.BeginFigure(new Point(0, Plot), true);
            for (var i = 0; i < count; i++) path.LineTo(new Point((i + 0.5) * Step, Plot - values[i] / largest * (Plot - 2)));
            path.LineTo(new Point(Bounds.Width, Plot));
            path.EndFigure(true);
        }
        using (context.PushOpacity(0.35)) context.DrawGeometry(Ui.Accent, null, area);
        var from = start * Step; var to = end * Step;
        using (context.PushOpacity(0.15)) context.DrawRectangle(Ui.Accent, null, new Rect(from, 0, Math.Max(0, to - from), Plot));
        context.DrawRectangle(null, new Pen(Ui.Accent, 1), new Rect(from, 0.5, Math.Max(0, to - from), Plot - 1));
        foreach (var edge in new[] { from, to })
            context.DrawRectangle(Ui.Accent, null, new Rect(Math.Clamp(edge - Handle / 2, 0, Bounds.Width - Handle), Plot / 2 - 12, Handle, 24), 2, 2);
        context.DrawRectangle(Ui.BorderBrush, null, new Rect(0, Plot + Gap, Bounds.Width, Track), 4, 4);
        context.DrawRectangle(Ui.Muted, null, new Rect(from, Plot + Gap, Math.Max(Track, to - from), Track), 4, 4);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (values.Count == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var x = e.GetPosition(this).X;
        var from = start * Step; var to = end * Step;
        dragging = Math.Abs(x - from) <= Handle ? 1 : Math.Abs(x - to) <= Handle ? 2 : x > from && x < to ? 3 : 4;
        anchor = x; anchorStart = start; anchorEnd = end;
        if (dragging == 4)
        {
            var size = end - start;
            var centred = Math.Clamp((int)Math.Round(x / Step) - size / 2, 0, values.Count - size);
            start = centred; end = centred + size;
            dragging = 3; anchor = x; anchorStart = start; anchorEnd = end;
            InvalidateVisual();
            RangeChanged?.Invoke(start, end, false);
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (dragging != 0) Move(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (dragging == 0) return;
        dragging = 0;
        e.Pointer.Capture(null);
        RangeChanged?.Invoke(start, end, true);
    }

    private void Move(double x)
    {
        var count = values.Count;
        var shift = (int)Math.Round((x - anchor) / Step);
        var (from, to) = dragging switch
        {
            1 => (Math.Clamp(anchorStart + shift, 0, anchorEnd - (int)MinWeeks), anchorEnd),
            2 => (anchorStart, Math.Clamp(anchorEnd + shift, anchorStart + (int)MinWeeks, count)),
            _ => (Math.Clamp(anchorStart + shift, 0, count - (anchorEnd - anchorStart)), Math.Clamp(anchorStart + shift, 0, count - (anchorEnd - anchorStart)) + anchorEnd - anchorStart)
        };
        if (from == start && to == end) return;
        start = from; end = to;
        InvalidateVisual();
        RangeChanged?.Invoke(start, end, false);
    }
}