namespace Ghostty.Avalonia;

/// <summary>Autoscroll of a drag selection: the pointer outside the grid scrolls the history, faster the farther out it is.</summary>
internal static class SelectionAutoscroll
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    private const int MaxLines = 10;

    /// <summary>Rows to scroll per tick for a pointer at <paramref name="y"/> in a grid of <paramref name="height"/>: negative toward the history, positive toward the prompt, zero inside.</summary>
    internal static int Lines(double y, double height, double cell)
    {
        var outside = y < 0 ? -y : y > height ? y - height : 0;
        if (outside <= 0) return 0;
        var lines = Math.Min(1 + (int)(outside / Math.Max(cell, 1)), MaxLines);
        return y < 0 ? -lines : lines;
    }
}