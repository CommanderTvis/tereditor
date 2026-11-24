using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Tereditor.Plugins.UI.Kit.Visualization;
using Tereditor.Scintilla;

namespace Tereditor.UI.Rendering;

/// <summary>
/// One view of diffs (the Changes pane, or a diff tab) zooms its own code text. It claims the zoom gestures over all of
/// itself, editors or not, and sizes only what <paramref name="apply"/> reaches, so no other editor, pane or the window's page
/// zoom follows. The size lives as long as the view and is not saved.
/// </summary>
internal sealed class TextZoomClaim(Action<double?> apply)
{
    private double? size;
    private double start, scale;
    private long last;

    internal double? Size => size;

    internal void Attach(Control view)
    {
        // Bubbling: nothing below claims it, as diff editors leave the gesture to their view.
        view.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, (_, e) => { Magnify(e.Delta.X); e.Handled = true; });
        view.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!ZoomGesture.IsZoom(e.KeyModifiers)) return;
            Magnify(e.Delta.Y / 10); e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>One step of a pinch or wheel zoom, accumulated against the size a gesture started at, like an editor's own.</summary>
    internal void Magnify(double delta)
    {
        if (!ScintillaEditor.ZoomGestures || !double.IsFinite(delta)) return;
        var now = Stopwatch.GetTimestamp();
        if (start == 0 || Stopwatch.GetElapsedTime(last, now) > TimeSpan.FromMilliseconds(250)) { start = size ?? ScintillaEditor.TextSize; scale = 1; }
        last = now;
        scale = Math.Clamp(scale + delta, ScintillaEditor.MinimumTextSize / start, ScintillaEditor.MaximumTextSize / start);
        size = Math.Clamp(Math.Round(start * scale * 4) / 4, ScintillaEditor.MinimumTextSize, ScintillaEditor.MaximumTextSize);
        apply(size);
    }
}