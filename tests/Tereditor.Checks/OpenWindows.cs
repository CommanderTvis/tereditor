using Avalonia.Controls;
using Avalonia.Threading;

namespace Tereditor.Checks;

/// <summary>
/// The windows a gate case opened and did not close. Cases share the process, and a window left behind keeps its
/// timers and animations: every later pump renders it again, which once took the gate past its CI time limit.
/// </summary>
internal static class OpenWindows
{
    private static readonly HashSet<Window> open = [];

    static OpenWindows()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => open.Add(window));
        Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => open.Remove(window));
    }

    internal static IReadOnlySet<Window> Snapshot() => open.ToHashSet();

    /// <summary>Closes every window opened since <paramref name="before"/>, or hides one that refuses, and names them.</summary>
    internal static IReadOnlyList<string> Discard(IReadOnlySet<Window> before)
    {
        if (open.IsSubsetOf(before)) return [];
        // A close the case posted is still due.
        Dispatcher.UIThread.RunJobs();
        var left = open.Except(before).ToArray();
        var names = left.Select(window => $"{window.GetType().Name} '{window.Title}'").ToArray();
        foreach (var window in left)
        {
            if (!open.Contains(window)) continue;
            window.Close();
            if (open.Remove(window)) window.Hide();
        }
        return names;
    }
}