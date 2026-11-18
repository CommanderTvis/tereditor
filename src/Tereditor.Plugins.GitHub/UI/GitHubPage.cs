using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// The layout every GitHub tab shares: a header (title and filter bar) that stays fixed at the top, and a scroller beneath it that holds the
/// list or conversation. A tab with no header shows only the scroller.
/// </summary>
internal sealed class GitHubPage : Grid
{
    internal const string HeaderName = "GitHubHeader";

    private readonly ContentControl header = new() { Name = HeaderName, IsVisible = false };

    public ScrollViewer Scroll { get; } = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    public GitHubPage()
    {
        RowDefinitions = new("Auto,*");
        header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SetRow(Scroll, 1);
        Children.Add(header);
        Children.Add(Scroll);
    }

    public void Show(Control? top, Control body)
    {
        header.Content = top;
        header.IsVisible = top is not null;
        Scroll.Content = body;
    }
}
