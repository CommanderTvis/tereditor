using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;

using Tereditor.Plugins.UI.Kit;
using Tereditor.UI.Panels;
using Tereditor.UI.Rendering;

namespace Tereditor.UI;

/// <summary>
/// The window without a top bar (<see cref="UnifiedChrome"/>): the controls the bar carried move into a header on top of the Projects
/// rail, and Settings leaves the window's corner for the gear in the header. The moved controls keep their names, so
/// every reader of them is unchanged.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private Grid? railLightsRow;

    /// <summary>The room above the rail's header where there is no title row for the window's buttons.</summary>
    internal const double RailTopInset = 8;

    private void WireUnifiedChrome()
    {
        var rootGrid = this.FindControl<Grid>("WorkbenchRoot")!;
        // The bar is gone from layout; its controls are re-parented below before the first rebuild draws them.
        this.FindControl<Border>("WindowTitleBar")!.IsVisible = false;
        rootGrid.RowDefinitions[0].Height = new GridLength(0);

        // A Mac has the native application menu (About, Settings), so only layouts without the controls at the left draw the Settings gear.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { NewAddProjectButton() } };
        if (!UnifiedChrome.ControlsAtLeft)
        {
            var gear = new Button
            {
                Name = "RailSettingsButton",
                Content = Ui.Icon("settings", null, 14),
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Ui.AsIconButton(gear);
            ((Border)gear.Content!).Bind(Border.BackgroundProperty, gear.GetObservable(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty));
            ToolTip.SetTip(gear, "Settings"); AutomationProperties.SetName(gear, "Settings");
            gear.Click += (_, _) => ShowSettings();
            actions.Children.Add(gear);
        }

        var header = new StackPanel { Name = "RailHeader", Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        // The first row holds the add button (and the gear). Where the window's controls are at the left (a Mac's traffic lights, over the
        // draggable title region) the buttons sit at the right end; elsewhere they sit at the left, after the room the system's chip takes.
        var controlsAtLeft = UnifiedChrome.ControlsAtLeft;
        railLightsRow = new Grid { Name = "RailLightsRow", ColumnDefinitions = new ColumnDefinitions(controlsAtLeft ? "*,Auto" : "Auto,*") };
        Ui.Place(railLightsRow, actions, 0, controlsAtLeft ? 1 : 0);
        header.Children.Add(railLightsRow);
        // Without a title row for the window's controls (the browser), the header keeps its own inset from the page edge.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsAndroid()) header.Margin = new Thickness(0, RailTopInset, 0, 8);

        var project = this.FindControl<Border>("ProjectSegment")!;
        var branch = this.FindControl<Border>("BranchSegment")!;
        foreach (var segment in new[] { project, branch })
        {
            ((Panel)segment.Parent!).Children.Remove(segment);
            segment.MaxWidth = double.PositiveInfinity;
            // The top bar gives each segment a fixed width; in the rail it follows the rail's width.
            segment.Width = double.NaN;
            segment.HorizontalAlignment = HorizontalAlignment.Stretch;
            segment.Padding = new Thickness(8, 0, 8, 0);
        }
        header.Children.Add(project);
        // Every block is one pattern: a caption over a value row with the same insets, icon column and height. The value pill fills the
        // rail's width, and the branch's sync state sits at the right end of its own row.
        foreach (var pill in new[] { "ScopeProject", "ScopeBranch" }) this.FindControl<Button>(pill)!.HorizontalAlignment = HorizontalAlignment.Stretch;
        // The branch and remote blocks are equal columns with the usual 8 gap; without a remote status the remote block is gone and
        // the branch block takes the whole row.
        var columns = this.FindControl<Grid>("BranchColumns")!;
        var remoteBlock = this.FindControl<Grid>("RemoteBlock")!;
        remoteBlock.Margin = default; remoteBlock.MinWidth = 0;
        this.FindControl<Button>("ScopeBranch")!.MinWidth = 0;
        // The grid always has three columns, so the remote block's column stays valid (a block placed past the last column is
        // clamped to the first and the grid keeps drawing it there); the unused two collapse to nothing.
        void Split() => columns.ColumnDefinitions = new ColumnDefinitions(remoteBlock.IsVisible ? "*,8,*" : "*,0,0");
        Split();
        Grid.SetColumn(remoteBlock, 2);
        remoteBlock.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) Split(); };
        // Where there is no branch to show (Project Home, a folder that is not a repository) the segment's room stays, empty, so the
        // blocks below it keep their place across every project.
        var reserve = new Border { Name = "BranchReserve", Height = 34, IsVisible = !branch.IsVisible, IsHitTestVisible = false };
        branch.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) reserve.IsVisible = !branch.IsVisible; };
        header.Children.Add(reserve);
        header.Children.Add(branch);

        // The plugins' header items (the credits) take the same captioned block under the branch.
        var tools = this.FindControl<StackPanel>("HeaderTools")!;
        var items = tools.Children.OfType<Plugins.PluginHeaderItems>().Single();
        tools.Children.Remove(items);
        var credits = new Border { Name = "CreditsSegment", Padding = new Thickness(8, 0, 8, 0), IsVisible = false };
        var creditsCaption = new TextBlock { Text = "Credits" };
        creditsCaption.Classes.Add("location-caption"); creditsCaption.Classes.Add("tr-text-metadata");
        var creditsRows = new Grid { RowDefinitions = new RowDefinitions("14,20"), VerticalAlignment = VerticalAlignment.Center };
        Ui.Place(creditsRows, creditsCaption); creditsRows.Children.Add(items); Grid.SetRow(items, 1);
        items.Orientation = Orientation.Vertical; items.HorizontalAlignment = HorizontalAlignment.Stretch;
        credits.Child = creditsRows;
        var adopted = new HashSet<Button>();
        items.LayoutUpdated += (_, _) =>
        {
            var buttons = items.GetLogicalDescendants().OfType<Button>().ToArray();
            credits.IsVisible = buttons.Any(button => button.IsVisible);
            foreach (var button in buttons.Where(adopted.Add))
            {
                // The value is a pill like the project's: the same inset, icon column and text role, filling the row.
                button.Padding = new Thickness(8, 0); button.Height = 20; button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Classes.Add("location-pill"); button.Classes.Add("tr-title-section");
                foreach (var text in button.GetLogicalDescendants().OfType<TextBlock>()) Typography.Apply(text, Typography.TitleSection);
            }
        };
        header.Children.Add(credits);

        // The Host block (the listener's mark and the connection state) is the last captioned block, with the segments' inset; it is
        // gone while it has nothing to show, so only the project list below it moves.
        var host = this.FindControl<Grid>("HostSegment")!;
        tools.Children.Remove(host);
        host.Margin = new Thickness(8, 0, 8, 0);
        header.Children.Add(host);

        // Settings opens from the gear or the application menu. The button stays in the
        // hidden title bar, which the window's Settings wiring and the other layouts share.
        SuppressButtonMenus(header);
        header.ContextMenu = NewViewMenu();
        // The header's context menu (right-click, long press) is the View menu, with Settings and About.
        // A panel with no background takes no pointer input, so the menu needs one for the header's empty space (Avalonia raises it on a touch hold).
        header.Background = Avalonia.Media.Brushes.Transparent;
#if !ANDROID && !BROWSER
        // The rail header is the window's status area: a double-click on its blank space zooms the window like the title bar does. Buttons keep
        // their clicks, and a Mac's first row is the native title region, which the system already zooms.
        header.PointerPressed += (_, e) =>
        {
            if (e.ClickCount != 2 || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed || e.Source is not Control source) return;
            if (!LinuxWindowChrome.StartsMove(source) || UnifiedChrome.ControlsAtLeft && (ReferenceEquals(railLightsRow, source) || railLightsRow!.IsLogicalAncestorOf(source))) return;
            e.Handled = true;
            TitleBarDoubleClicked();
        };
#endif
        viewMenu = header.ContextMenu;
        surface.LeftHeader = header;
        // The strip at the top right shares the caption's row with the system's controls; its buttons take their touches there too.
        surface.LayoutUpdated += (_, _) => ReportCaption();
        ApplyChromeZoom();
    }

    /// <summary>
    /// The native traffic lights do not zoom, so the lights row grows with the zoom out and keeps its physical height;
    /// without chrome (the browser) there is no such row.
    /// </summary>
    private void ApplyUnifiedChromeZoom(double zoom)
    {
        this.FindControl<Grid>("WorkbenchRoot")!.RowDefinitions[0].Height = new GridLength(0);
        if (railLightsRow is not null && (OperatingSystem.IsAndroid() || captionBar is not null))
        {
            // An Android desktop window's caption bar does not zoom either: the row is as tall as it, clear of the app chip at the
            // left, and the strip at the top right keeps clear of the system's buttons.
            railLightsRow.Height = captionBar is { } bar ? bar.RowHeight(zoom, LayoutRoles.TopbarRow) : LayoutRoles.TopbarRow;
            railLightsRow.Margin = new Thickness(captionBar is { } chip ? chip.Left / zoom : 0, 0, 0, 0);
            if (surface is { } dock) dock.TopRightReserve = captionBar is { } controls ? (controls.Height / zoom, controls.Right / zoom) : default;
            return;
        }
        if (surface is { } plain) plain.TopRightReserve = default;
        if (!OperatingSystem.IsMacOS()) return;
        if (railLightsRow is not null) railLightsRow.Margin = default;
        var height = Math.Max(LayoutRoles.TopbarRow, LayoutRoles.TopbarRow / zoom);
        ExtendClientAreaTitleBarHeightHint = height * zoom;
        if (railLightsRow is not null) railLightsRow.Height = height;
    }
}