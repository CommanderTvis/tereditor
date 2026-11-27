using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks;

/// <summary>
/// A submenu lies beside its parent menu: its visible frame starts where the parent's ends, with no part over or under the parent and no gap
/// (within a pixel of rounding under page zoom), to the right while the window has room there and to the left otherwise. Native popup windows
/// can stack the parent over the child, so any overlap hides part of the child. Runs in popup windows (<c>--menu-popups</c>) and in the
/// overlay the browser and Android use (<c>--menu-layout</c>).
/// </summary>
internal static class SubmenuPlacementChecks
{
    private const double Width = 1000, Height = 500;

    private sealed record Frame(double Left, double Top, double Right, double Bottom);

    internal static void Run(bool overlay = false)
    {
        var failures = new List<string>();
        // A headless popup window keeps scale 1 whatever its owner has, so only page zoom varies.
        foreach (var zoom in new[] { 1.0, 1.5 })
        {
            var tag = $"zoom {zoom}";
            // The window has screen room on both sides, so only its own edges can turn a submenu.
            Case($"{tag}, left edge", zoom, HorizontalAlignment.Left, VerticalAlignment.Center, false, [true, true], failures);
            Case($"{tag}, bottom left", zoom, HorizontalAlignment.Left, VerticalAlignment.Bottom, false, [true, true], failures);
            Case($"{tag}, right edge", zoom, HorizontalAlignment.Right, VerticalAlignment.Center, false, [false, false], failures);
            Case($"{tag}, flyout at the left edge", zoom, HorizontalAlignment.Left, VerticalAlignment.Center, true, [true, true], failures);
            Case($"{tag}, flyout at the right edge", zoom, HorizontalAlignment.Right, VerticalAlignment.Center, true, [false, false], failures);
        }
        if (!overlay)
        {
            // Too narrow for the submenu on either side: it leaves the window on the roomier side rather than going under its parent.
            Case("narrow window, left edge", 1, HorizontalAlignment.Left, VerticalAlignment.Center, false, [true], failures, width: 340);
            Case("narrow window, right edge", 1, HorizontalAlignment.Right, VerticalAlignment.Center, false, [false], failures, width: 340);
            // A window reaching past the screen's right edge: the screen turns the submenu, and its own submenu follows.
            Case("screen edge", 1, HorizontalAlignment.Left, VerticalAlignment.Center, false, [false, false], failures, x: 1920 - 320);
        }
        Require(failures.Count == 0, "Submenus must open beside their parent menu:\n" + string.Join("\n", failures));
        Console.WriteLine($"PASS submenu placement ({(overlay ? "overlay" : "popup windows")}): submenus and nested submenus lie flush beside their parent, to the right or turned to the left, again on reopening");
    }

    private static MenuItem Row(string header, params MenuItem[] children)
    {
        var item = new MenuItem { Header = header };
        foreach (var child in children) item.Items.Add(child);
        return item;
    }

    private static void Case(string name, double zoom, HorizontalAlignment horizontal, VerticalAlignment vertical, bool flyout, bool[] right, List<string> failures, double width = Width, int x = 400)
    {
        var anchor = new Button { Content = "x", Width = 30, Height = 24, HorizontalAlignment = horizontal, VerticalAlignment = vertical };
        var window = new Window
        {
            Width = width,
            Height = Height,
            Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(zoom, zoom), Child = new Grid { Children = { anchor } } }
        };
        window.Show();
        window.Position = new PixelPoint(x, 100);
        Settle();
        var nested = Row("Nested", Row("Deeper one"), Row("Deeper two"));
        var advisor = Row("Advisor", Row("None"), Row("Opus"), nested);
        var rows = new[] { Ui.IconMenu("Continue the last conversation", () => { }, ActionIcon.Add), Row("Teleport a session here"), advisor, Row("With custom arguments") };
        ContextMenu? menu = null;
        MenuFlyout? flat = null;
        if (flyout) { flat = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft }; foreach (var row in rows) flat.Items.Add(row); }
        else { menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = anchor }; foreach (var row in rows) menu.Items.Add(row); anchor.ContextMenu = menu; }
        try
        {
            if (menu is not null) menu.Open(anchor); else flat!.ShowAt(anchor);
            Settle();
            foreach (var pass in new[] { "first open", "reopened" })
            {
                var levels = new[] { advisor, nested }.Take(right.Length).ToArray();
                for (var level = 0; level < levels.Length; level++)
                {
                    var item = levels[level];
                    item.IsSubMenuOpen = true;
                    Settle();
                    var popup = item.GetVisualDescendants().OfType<Popup>().Single(part => part.Name == "PART_Popup");
                    var parent = Screen(item.GetVisualAncestors().OfType<Control>().First(part => part.Parent is Popup));
                    var child = Screen((Control)popup.Child!);
                    var area = Screen(window, new Size(window.ClientSize.Width, window.ClientSize.Height));
                    var gap = right[level] ? child.Left - parent.Right : parent.Left - child.Right;
                    var label = $"{name}, level {level + 1}, {pass}: parent {parent}, child {child}, window {area}, offset {popup.HorizontalOffset:0.##}, gap {gap:0.##} px";
                    if (!item.IsSubMenuOpen || !popup.IsOpen) failures.Add(label + " (not open)");
                    else if (Math.Abs(gap) > (zoom == 1 ? 0.5 : 1)) failures.Add(label + $" (not flush on the {(right[level] ? "right" : "left")})");
                    else if ((width >= Width && x == 400 && (child.Left < area.Left - 1 || child.Right > area.Right + 1)) || child.Top < area.Top - 1 || child.Bottom > area.Bottom + 1) failures.Add(label + " (outside the window)");
                }
                advisor.IsSubMenuOpen = false;
                nested.IsSubMenuOpen = false;
                Settle();
            }
        }
        finally
        {
            menu?.Close();
            flat?.Hide();
            window.Close();
            Settle();
        }
    }

    private static Frame Screen(Control control) => Screen(control, control.Bounds.Size);

    private static Frame Screen(Control control, Size size)
    {
        var top = TopLevel.GetTopLevel(control)!;
        var low = top.PointToScreen(control.TranslatePoint(default, top)!.Value);
        var high = top.PointToScreen(control.TranslatePoint(new Point(size.Width, size.Height), top)!.Value);
        return new(low.X, low.Y, high.X, high.Y);
    }

    private static void Settle()
    {
        for (var pass = 0; pass < 5; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}