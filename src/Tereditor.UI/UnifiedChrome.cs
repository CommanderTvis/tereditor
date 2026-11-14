namespace Tereditor.UI;

/// <summary>
/// Whether the window has no top bar: its left column is the fixed Projects rail with the project and branch
/// controls on top, and Settings sits behind the gear (or the application menu on a Mac) and About in Settings and the header's context menu. On by default on macOS, on Android and in the browser (a phone-sized
/// Android workbench keeps its pages, which have no rail);
/// the other platforms keep the top bar and the dock's left column. Checks override it to run either layout on any OS.
/// </summary>
public static class UnifiedChrome
{
    public static bool? Override { get; set; }

    /// <summary>Checks set it to run either header layout on any OS.</summary>
    public static bool? ControlsAtLeftOverride { get; set; }

    /// <summary>
    /// Whether the window's own controls (a Mac's traffic lights) sit at the left of the rail header's first row. Then the add button is at
    /// the row's right end and there is no gear. Otherwise the add button and the gear sit together at the left, after any room the system reserves.
    /// </summary>
    public static bool ControlsAtLeft => ControlsAtLeftOverride ?? OperatingSystem.IsMacOS();

    public static bool Enabled => Override ?? (OperatingSystem.IsMacOS() || OperatingSystem.IsBrowser() || OperatingSystem.IsAndroid());
}