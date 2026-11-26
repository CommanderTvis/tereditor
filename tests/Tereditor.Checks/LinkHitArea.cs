using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Tereditor.Checks;

// Real, hit-tested pointer input on a link and on the empty space beside it.
internal static class LinkHitArea
{
    internal static void Check(Window window, Control link, Func<int> activations, string what)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        // A link stretched across its row would put the point beside it outside the stretched control, so its width is checked first.
        var text = link as TextBlock ?? (TextBlock)((ContentControl)link).Content!;
        Require(link.Bounds.Width <= text.TextLayout.WidthIncludingTrailingWhitespace + 1,
            $"{what}: the link is as wide as its text ({link.Bounds.Width} against {text.TextLayout.WidthIncludingTrailingWhitespace}).");
        var on = link.TranslatePoint(new Point(link.Bounds.Width / 2, link.Bounds.Height / 2), window)!.Value;
        var beside = link.TranslatePoint(new Point(link.Bounds.Width + 12, link.Bounds.Height / 2), window)!.Value;
        var before = activations();
        var hand = Click(window, beside);
        Require(!hand && activations() == before && !link.IsPointerOver, $"{what}: the empty space beside the link has no hand cursor, no hover and opens nothing.");
        hand = Click(window, on);
        Require(hand && activations() == before + 1, $"{what}: the link has the hand cursor and opens on a click (hand {hand}, opened {activations() - before}).");
    }

    // Moves to the point, reads whether the hand cursor shows there, and left-clicks.
    private static bool Click(Window window, Point point)
    {
        window.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var cursor = (window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().OfType<InputElement>().Select(element => element.Cursor).FirstOrDefault(found => found is not null);
        var hand = cursor?.ToString() == nameof(StandardCursorType.Hand);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        return hand;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}