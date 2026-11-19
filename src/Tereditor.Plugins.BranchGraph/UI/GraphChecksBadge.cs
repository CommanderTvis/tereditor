using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.BranchGraph.UI;

/// <summary>The small CI status after a commit's subject: an icon by state, a tooltip with the counts and the names that need a look, a click that opens the checks.</summary>
internal sealed class GraphChecksBadge : Border
{
    private const int Names = 5;
    private string url = "";

    internal GraphChecksBadge(Action<string> open)
    {
        Name = "GraphChecks";
        Margin = new Thickness(8, 0, 0, 0);
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent;
        // The row is a button; the badge takes its own click so the row's one does not also fire.
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && url.Length > 0) { open(url); e.Handled = true; } };
        PointerReleased += (_, e) => e.Handled = true;
    }

    internal string? Glyph { get; private set; }
    internal IBrush? Tint { get; private set; }
    internal string? Tip { get; private set; }

    internal void Update(GitGraphCheck? checks)
    {
        IsVisible = checks is not null;
        if (checks is null) { Child = null; Glyph = null; Tip = null; url = ""; return; }
        (Glyph, Tint) = Look(checks.State);
        Tip = Describe(checks);
        url = checks.Url.StartsWith("https://", StringComparison.Ordinal) ? checks.Url : "";
        Child = Ui.Icon(Glyph, Tint, 16);
        ToolTip.SetTip(this, Tip);
        AutomationProperties.SetName(this, Tip.Split('\n')[0]);
    }

    internal static (string Glyph, IBrush Tint) Look(string state) => state switch
    {
        "success" => ("checkboxCircleFill", Ui.Success),
        "failure" => ("closeCircleFill", Ui.Danger),
        "pending" => ("loader", Ui.Warning),
        _ => ("recordCircle", Ui.Muted)
    };

    internal static string Describe(GitGraphCheck checks)
    {
        var text = $"CI: {checks.Passed} of {checks.Total} checks passed";
        if (checks.Attention.Count == 0) return text;
        var shown = string.Join('\n', checks.Attention.Take(Names));
        return text + "\n" + shown + (checks.Attention.Count > Names ? $"\nand {checks.Attention.Count - Names} more" : "");
    }
}