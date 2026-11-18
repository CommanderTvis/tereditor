using System.Globalization;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>What the Actions list and a run's page share: status icons and colours, times, durations and opening links.</summary>
internal static class GitHubRuns
{
    public static string Glyph(string state) => state switch
    {
        "success" or "answered" => GitHubIcons.CheckboxCircleFill,
        "failure" => GitHubIcons.CloseCircleFill,
        "cancelled" => GitHubIcons.StopCircle,
        "skipped" => GitHubIcons.SkipForward,
        "running" => GitHubIcons.RecordCircle,
        "queued" => GitHubIcons.Time,
        "closed" => GitHubIcons.CheckboxCircle,
        _ => GitHubIcons.CheckboxBlankCircle
    };

    public static IBrush Tone(string state) => state switch
    {
        "success" or "answered" or "open" => Ui.Success,
        "failure" => Ui.Danger,
        "running" => Ui.Warning,
        _ => Ui.Muted
    };

    public static string StateText(string state) => state switch
    {
        "success" => "Success",
        "failure" => "Failed",
        "cancelled" => "Cancelled",
        "skipped" => "Skipped",
        "running" => "In progress",
        "queued" => "Queued",
        _ => ""
    };

    /// <summary>"Today at 2:33 PM" for the same day as now, else "Nov 17, 9:42 PM GMT+1"; pure, so a check can pin the zone and the clock.</summary>
    public static string Started(string iso, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) return "";
        var local = TimeZoneInfo.ConvertTime(time, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        var clock = local.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (local.Date == today.Date) return "Today at " + clock;
        var offset = local.Offset;
        var gmt = "GMT" + (offset < TimeSpan.Zero ? "-" : "+") + Math.Abs(offset.Hours) + (offset.Minutes != 0 ? ":" + Math.Abs(offset.Minutes).ToString("00", CultureInfo.InvariantCulture) : "");
        return local.ToString("MMM d", CultureInfo.InvariantCulture) + ", " + clock + " " + gmt;
    }

    /// <summary>Time since a start, as "2m 5s", for a step or job that is still running.</summary>
    public static string Elapsed(string iso, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) || now < start) return "";
        var span = now - start;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }

    /// <summary>Opens a github.com page in the system browser, off the UI thread; any other address is ignored.</summary>
    public static void Open(string url)
    {
        if (!url.StartsWith("https://github.com/", StringComparison.Ordinal)) return;
        _ = Task.Run(() =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        });
    }

    /// <summary>Text that reads as a link: accent with an underline while the pointer is over it.</summary>
    public static TextBlock Link(string text, Action click, IBrush? rest = null, double size = 12)
    {
        var block = Ui.Text(text, rest ?? Ui.Accent, size);
        block.Cursor = new Cursor(StandardCursorType.Hand);
        block.PointerEntered += (_, _) => block.TextDecorations = TextDecorations.Underline;
        block.PointerExited += (_, _) => block.TextDecorations = null;
        block.PointerPressed += (_, e) => { if (e.GetCurrentPoint(block).Properties.IsLeftButtonPressed) { e.Handled = true; click(); } };
        return block;
    }
}