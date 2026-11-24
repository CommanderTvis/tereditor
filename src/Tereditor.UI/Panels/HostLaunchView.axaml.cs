using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI.Panels;

/// <summary>The connect screen's commands for starting a host, with the visitor's own system first and a Copy button on each.</summary>
public sealed partial class HostLaunchView : UserControl
{
    private readonly Func<string, Task<bool>> copy;

    // Compiled XAML never uses the runtime loader; this public constructor only makes Avalonia register the view (AVLN3001).
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Obsolete("Construct HostLaunchView with its arguments.", true)]
    public HostLaunchView() => throw new System.NotSupportedException("HostLaunchView needs its constructor arguments.");

    /// <param name="os">The visitor's system, from <see cref="HostLaunch.Detect"/>.</param>
    /// <param name="copy">Puts text on the clipboard; true when it worked. The browser passes its own, with a fallback.</param>
    public HostLaunchView(HostOs os, Func<string, Task<bool>> copy)
    {
        this.copy = copy;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("LaunchPairing")!.Text = HostLaunch.PairingNotice;
        this.FindControl<TextBlock>("LaunchNotice")!.Text = HostLaunch.LocalNetworkNotice;
        var (first, other) = HostLaunch.Order(os);
        var firstPanel = this.FindControl<StackPanel>("LaunchFirst")!;
        foreach (var option in first) firstPanel.Children.Add(Card(option));
        var otherPanel = this.FindControl<StackPanel>("LaunchOther")!;
        foreach (var option in other) otherPanel.Children.Add(Card(option, withSystem: true));
        // The uninstall commands sit under the start commands: the visitor's own first, the rest under Other options.
        var own = HostLaunch.Uninstall.Where(option => option.Os == os).ToArray();
        var uninstallPanel = this.FindControl<StackPanel>("LaunchUninstall")!;
        foreach (var option in own) uninstallPanel.Children.Add(Card(option));
        this.FindControl<TextBlock>("LaunchUninstallHeading")!.IsVisible = own.Length > 0;
        foreach (var option in HostLaunch.Uninstall.Where(option => option.Os != os)) otherPanel.Children.Add(Card(option, withSystem: true));
        this.FindControl<Expander>("LaunchOtherExpander")!.IsVisible = otherPanel.Children.Count > 0;
    }

    private Control Card(LaunchOption option, bool withSystem = false)
    {
        var card = new StackPanel { Name = "Launch_" + option.Id, Spacing = 6 };
        card.Children.Add(new TextBlock { Text = withSystem ? option.Os + ": " + option.Title : option.Id.EndsWith("-uninstall") ? "Command" : option.Title, FontSize = 16 });
        card.Children.Add(new TextBlock { Text = option.Steps, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Ui.Muted });
        var box = new TextBox { Text = option.Command, IsReadOnly = true, AcceptsReturn = true, FontFamily = Ui.CodeFont, FontSize = 12, MaxHeight = 160, Name = "LaunchCommand_" + option.Id };
        var copied = new TextBlock { Name = "LaunchCopied_" + option.Id, Text = "Copied", IsVisible = false, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Name = "LaunchCopy_" + option.Id, Content = "Copy", Height = 36, MinWidth = 72 };
        button.Click += async (_, _) =>
        {
            var ok = await copy(option.Command);
            copied.Text = ok ? "Copied" : "Copy failed. Select the text and copy it by hand.";
            copied.IsVisible = true;
            if (!ok) box.Focus();
            await Task.Delay(2500);
            copied.IsVisible = false;
        };
        card.Children.Add(box);
        card.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { button, copied } });
        return card;
    }
}