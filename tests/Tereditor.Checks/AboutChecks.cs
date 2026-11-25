using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Tereditor.UI;
using Tereditor.UI.Panels;

namespace Tereditor.Checks;

internal static class AboutChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Pump(int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    internal static void Run()
    {
        // A build without a stamp reads "dev"; a stamped one reads as stamped.
        Require(AboutInfo.ParseVersion(null) == "dev" && AboutInfo.ParseVersion("") == "dev" && AboutInfo.ParseVersion("  ") == "dev", "Missing version text must read dev.");
        Require(AboutInfo.ParseVersion("0.2.0+20260101-abc1234") == "0.2.0+20260101-abc1234", "A stamped version must be shown as stamped.");
        Require(AboutInfo.Version.Length > 0, "The assembly version must not be empty.");

        var runtime = AboutInfo.Runtime();
        Require(runtime.Any(line => line.Label == "Runtime") && runtime.Any(line => line.Label == "OS") && runtime.Any(line => line.Label == "Avalonia"), "Runtime lines must name the runtime, OS and Avalonia.");
        var details = AboutInfo.Details("9.9.9-test", runtime);
        Require(details.StartsWith("tereditor 9.9.9-test\n", StringComparison.Ordinal) && runtime.All(line => details.Contains($"{line.Label}: {line.Value}")), "Copy details must carry the name, version and every runtime line.");

        var fields = new AboutFields("9.9.9-test", runtime);
        var texts = fields.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToList();
        Require(texts.Contains("tereditor") && texts.Contains("Version 9.9.9-test") && texts.Contains(AboutInfo.Description), "The About view must show name, version and description.");
        Require(texts.Any(text => text.Contains("Runtime: .NET") && text.Contains("OS: ") && text.Contains("Avalonia: ")), "The About view must show the runtime lines.");
        Require(fields.Details == details, "The view's copy text must equal the details text.");
        var buttons = fields.GetLogicalDescendants().OfType<Button>().Select(button => button.Name).ToList();
        Require(buttons.Contains("AboutNotices") && buttons.Contains("AboutCopy") && buttons.Contains("AboutRepository") && buttons.Contains("AboutUpstream"), "The About view must offer notices, copy and both links.");
        var links = fields.GetLogicalDescendants().OfType<Button>().Where(button => button.Tag is Uri).Select(button => ((Uri)button.Tag!).AbsoluteUri.TrimEnd('/')).ToList();
        Require(links.Contains(AboutInfo.Repository) && links.Contains(AboutInfo.Upstream) && links.Contains(AboutInfo.NewIssue), "The links must target the repository, upstream ThinkRail and the new-issue page.");

        // Report an issue is the primary way the Help menu's Report an Issue… follows (`--shell` picks that item): both open the new-issue page through the shared launch.
        var launched = new List<(TopLevel Top, Uri Uri)>();
        var launch = LanguageProviderLinks.Launch;
        LanguageProviderLinks.Launch = (top, uri) => { launched.Add((top, uri)); return Task.CompletedTask; };
        try
        {
            var host = new Window { Content = fields };
            host.Show();
            var issue = fields.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "AboutIssue");
            Require(issue.IsVisible && ((Uri)issue.Tag!).AbsoluteUri == AboutInfo.NewIssue, "Report an issue must show and target AboutInfo.NewIssue.");
            issue.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Require(launched.Count == 1 && launched[0].Top == host && launched[0].Uri.AbsoluteUri == AboutInfo.NewIssue, "Report an issue must open the new-issue page from its window.");
            host.Close();
            host.Content = null;
        }
        finally { LanguageProviderLinks.Launch = launch; }

        // The frog says tero-tero: a floating bubble, no layout shift, gone after the timeout.
        var frogHost = new Window { Content = fields, Width = 600, Height = 500 };
        frogHost.Show();
        Dispatcher.UIThread.RunJobs();
        var frog = fields.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "AboutFrog");
        var speech = fields.GetLogicalDescendants().OfType<Popup>().Single(popup => popup.Name == "AboutSpeech");
        var version = fields.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "AboutVersion");
        var before = version.Bounds;
        Require(Avalonia.Automation.AutomationProperties.GetName(frog) == "tereditor logo" && frog.Focusable && !speech.IsOpen && speech.IsLightDismissEnabled, "The frog is a focusable button named tereditor logo; the bubble starts closed and closes on an outside click.");
        var spoken = AboutFields.SpeechDuration;
        AboutFields.SpeechDuration = TimeSpan.FromMilliseconds(300);
        try
        {
            frog.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Require(speech.IsOpen && speech.Child!.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "tero-tero!"), "Clicking the frog shows the tero-tero! bubble.");
            Require(version.Bounds == before, "The bubble must not move the version text.");
            Pump(150);
            frog.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump(200);
            Dispatcher.UIThread.RunJobs();
            Require(speech.IsOpen, "A second click restarts the timer.");
            Pump(400);
            Dispatcher.UIThread.RunJobs();
            Require(!speech.IsOpen, "The bubble closes after the timeout.");
            Require(version.Bounds == before, "Closing the bubble must not move the version text.");
        }
        finally { AboutFields.SpeechDuration = spoken; frogHost.Close(); }

        // The macOS application menu: About tereditor in place of the framework's About Avalonia.
        var settings = 0;
        var menu = ApplicationMenu.AppMenu(() => { }, () => settings++);
        var headers = menu.Items.OfType<NativeMenuItem>().Where(item => item is not NativeMenuItemSeparator).Select(item => item.Header ?? "").ToList();
        Require(headers.SequenceEqual(["About tereditor", "Settings…"]) && menu.Items[1] is NativeMenuItemSeparator, "The application menu must hold About tereditor, then Settings.");
        var preferences = menu.Items.OfType<NativeMenuItem>().Single(item => item.Header == "Settings…");
        Require(preferences.Gesture is { } chord && chord.Equals(WindowShortcuts.Settings.Gesture), "Settings shows the window's Settings chord.");
        var gear = ApplicationMenu.Gear(16, 2);
        Require(gear.PixelSize == new Avalonia.PixelSize(32, 32) && gear.Size == new Avalonia.Size(16, 16), "The gear is 16 pt at 2x.");
        var ring = new byte[32 * 32 * 4];
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(ring.Length);
        try { gear.CopyPixels(new Avalonia.PixelRect(0, 0, 32, 32), buffer, ring.Length, 32 * 4); System.Runtime.InteropServices.Marshal.Copy(buffer, ring, 0, ring.Length); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        byte Alpha(int x, int y) => ring[(y * 32 + x) * 4 + 3];
        Require(Alpha(16, 16 - 8) == 0 && Alpha(16 + 8, 16) == 0, "The gear is an outline: the band between the hub ring and the teeth' roots is empty, not filled.");
        Require(Enumerable.Range(0, 32).Any(x => Alpha(x, 16) > 128), "The gear outline draws opaque strokes.");
        Require(OperatingSystem.IsMacOS() ? preferences.Icon is { Size: { Width: 16, Height: 16 } } : preferences.Icon is null, "Settings carries the gear on macOS only.");
        var drawn = 0;
        Require(ApplicationMenu.MenuIcon(false, () => throw new InvalidOperationException("no system symbol off macOS"), () => throw new InvalidOperationException("no fallback off macOS")) is null, "Off macOS a menu item has no icon and asks for none.");
        Require(ApplicationMenu.MenuIcon(true, () => null, () => { drawn++; return gear; }) == gear && drawn == 1, "Without the system symbol the drawn gear stands in.");
        Require(ApplicationMenu.MenuIcon(true, () => gear, () => { drawn++; return null!; }) == gear && drawn == 1, "With the system symbol the fallback is not drawn.");
        Require(OperatingSystem.IsMacOS() || SystemSymbol.Render("gearshape", 16, 3) is null, "Off macOS the system symbol is unavailable.");
        if (OperatingSystem.IsMacOS() && SystemSymbol.Render("gearshape", 16, 3) is { } symbol)
            Require(symbol.PixelSize == new Avalonia.PixelSize(48, 48) && symbol.Size == new Avalonia.Size(16, 16), "The system gear is 16 pt at 3x.");
        Require(menu.Items.OfType<NativeMenuItem>().Where(item => item != preferences).All(item => item.Icon is null), "Only Settings has an icon.");
        ((INativeMenuItemExporterEventsImplBridge)preferences).RaiseClicked();
        Require(settings == 1, "Settings runs its command.");
        Require(!headers.Any(header => header.Contains("Avalonia", StringComparison.OrdinalIgnoreCase)), "The application menu must not offer About Avalonia.");

        // The dialog closes with Escape.
        var window = Dialogs.About(null);
        Dispatcher.UIThread.RunJobs();
        Require(window.IsVisible && window.GetLogicalDescendants().OfType<AboutFields>().Any(), "The About dialog must show the About view.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Require(!window.IsVisible, "Escape must close the About dialog.");
    }
}