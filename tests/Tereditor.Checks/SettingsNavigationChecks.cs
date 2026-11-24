using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

using Tereditor.Checks.E2E;
using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;
using Tereditor.UI;
using Tereditor.UI.Panels;
using Tereditor.UI.State;

using static Tereditor.Checks.E2E.E2eWorkspace;

namespace Tereditor.Checks;

// The real Settings view with every built-in section: at the default navigation column every label fits in full, and
// in a column too narrow for the longest label a label ends in an ellipsis with its full text as a tooltip. In both,
// each item's icon and label stay inside the item, and each item inside the navigation pane.
internal static class SettingsNavigationChecks
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "settings-navigation");
        Directory.CreateDirectory(directory);
        var host = new ProjectServices(directory);
        var window = new WorkbenchWindow(host, directory, new ProfileStore(directory + "-profile"), E2eTerminals.Plain);
        window.Width = 1000; window.Height = 700; window.Show();
        Until(() => window.WorkspaceMounted);
        var settings = new SettingsView(window, () => { }) { Width = 832, Height = 560 };
        window.ShowInOverlay(settings);
        try
        {
            Dispatcher.UIThread.RunJobs(); settings.UpdateLayout();
            var pane = settings.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "SettingsNavigationPane");
            var columns = settings.GetLogicalDescendants().OfType<Grid>().Single(control => control.Name == "SettingsColumns");
            Until(() => pane.GetLogicalDescendants().OfType<Button>().Any(button => button.Name?.StartsWith("Settings_plugin_", StringComparison.Ordinal) == true));
            var buttons = pane.GetLogicalDescendants().OfType<Button>().ToArray();
            Require(buttons.Any(button => button.Name?.StartsWith("Settings_plugin_", StringComparison.Ordinal) == true) && buttons.Length >= 12,
                $"The built-in sections must all be listed, found {buttons.Length}.");

            var trimmed = Verify(settings, pane, "default");
            Require(trimmed.Count == 0, $"At the default navigation width no label may be trimmed, but these are: {string.Join(", ", trimmed)}.");

            columns.ColumnDefinitions[0].Width = new GridLength(120);
            Dispatcher.UIThread.RunJobs(); settings.UpdateLayout();
            trimmed = Verify(settings, pane, "narrow");
            Require(trimmed.Count > 0, "At 120 px at least the longest label must be trimmed, so the check sees an overflow.");
            Reselect(settings, pane);
        }
        finally
        {
            settings.Close();
            window.Close();
        }
    }

    // Choosing the open page again builds nothing and keeps its content; another page and back builds each; a forced refresh rebuilds.
    private static void Reselect(SettingsView settings, Border pane)
    {
        Button Row(string name) => pane.GetLogicalDescendants().OfType<Button>().First(button => button.Name == name);
        void Click(string name) { Row(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
        var body = settings.GetLogicalDescendants().OfType<ContentControl>().First(control => control.Name == "SettingsBody");
        foreach (var name in pane.GetLogicalDescendants().OfType<Button>().Select(button => button.Name!).Where(name => name.StartsWith("Settings_", StringComparison.Ordinal)).ToArray())
        {
            Click(name);
            var builds = settings.PageBuilds; var page = body.Content;
            Click(name);
            Require(settings.PageBuilds == builds && ReferenceEquals(body.Content, page), $"Choosing {name} again must not rebuild its page.");
        }
        Click("Settings_Editor");
        var editor = body.Content; var count = settings.PageBuilds;
        Click("Settings_Terminal");
        Require(settings.PageBuilds == count + 1 && !ReferenceEquals(body.Content, editor), "Another page must be built.");
        Click("Settings_Editor");
        Require(settings.PageBuilds == count + 2 && body.Content is not null, "Going back must build the page again.");
        settings.ShowSection("Editor");
        Require(settings.PageBuilds == count + 3, "A forced ShowSection must still rebuild the page.");
    }

    // Returns the labels that are trimmed; throws for any clipped without an ellipsis and tip.
    private static List<string> Verify(SettingsView settings, Border pane, string width)
    {
        var trimmed = new List<string>();
        foreach (var button in pane.GetLogicalDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
        {
            var label = button.GetLogicalDescendants().OfType<TextBlock>().Single();
            var content = (Control)button.Content!;
            var inPane = button.TranslatePoint(default, pane)!.Value;
            Require(inPane.X >= -.01 && inPane.X + button.Bounds.Width <= pane.Bounds.Width + .01, $"[{width}] \"{label.Text}\" item {button.Bounds} is outside the navigation pane {pane.Bounds}.");
            foreach (var part in content.GetLogicalDescendants().OfType<Control>().Where(part => part.IsEffectivelyVisible && part.Bounds.Width > 0).Prepend(label).Distinct())
            {
                var at = part.TranslatePoint(default, button)!.Value;
                var inner = button.Bounds.Width - button.Padding.Right;
                Require(at.X >= button.Padding.Left - .01 && at.X + part.Bounds.Width <= inner + .01, $"[{width}] \"{label.Text}\": {part.GetType().Name} spans {at.X:0.#} to {at.X + part.Bounds.Width:0.#} outside the item's content {button.Padding.Left} to {inner:0.#}.");
            }
            var natural = new TextLayout(label.Text!, new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, label.Foreground).Width;
            var clipped = natural > label.Bounds.Width + .5;
            Require(!clipped || label.TextTrimming == TextTrimming.CharacterEllipsis, $"[{width}] \"{label.Text}\" is clipped without an ellipsis.");
            Require(clipped == (ToolTip.GetTip(label) is string tip && tip == label.Text), $"[{width}] \"{label.Text}\" {(clipped ? "is trimmed and needs its full text as a tooltip" : "fits and must have no tooltip")}.");
            if (clipped) trimmed.Add(label.Text!);
        }
        return trimmed;
    }
}