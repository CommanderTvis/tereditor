using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// Wraps a tab's scroll area with a pinned line of fixed height above it, so the text never moves the content. It says
/// "Updating…" while the host refreshes a saved answer, and "Showing saved data from …" with Retry when gh could not be reached.
/// </summary>
internal sealed class GitHubFreshness : Grid
{
    private readonly TextBlock text = Ui.Text("", Ui.Muted, 12);
    private readonly Button retryButton;
    private readonly Action refresh;

    public GitHubFreshness(Control content, Action refresh)
    {
        this.refresh = refresh;
        Name = "GitHubFreshness";
        RowDefinitions = new("Auto,*");
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        retryButton = Ui.Button("Retry", () => { Show("Updating…", false); refresh(); });
        retryButton.IsVisible = false;
        retryButton.Padding = new Thickness(8, 0);
        var line = new DockPanel { Height = 24, Margin = new Thickness(24, 0), LastChildFill = true };
        DockPanel.SetDock(retryButton, Dock.Right);
        line.Children.Add(retryButton);
        line.Children.Add(text);
        Children.Add(line);
        SetRow(content, 1);
        Children.Add(content);
    }

    /// <summary>The line for an answer. A saved answer that is being refreshed asks again at once, after the caller has drawn it.</summary>
    public void Apply(GitHubCacheNote? note)
    {
        if (note is null) Show("", false);
        else if (note.Refreshing)
        {
            Show("Updating…", false);
            Dispatcher.UIThread.Post(refresh, DispatcherPriority.Background);
        }
        else Show($"Showing saved data from {Ago(note.FetchedAt)}. {note.Reason}".TrimEnd(), true);
    }

    internal string Line => text.Text ?? "";

    private void Show(string message, bool retry)
    {
        text.Text = message;
        retryButton.IsVisible = retry;
    }

    private static string Ago(string iso) => DateTimeOffset.TryParse(iso, out var time) ? Ui.RelativeTime(time, DateTimeOffset.UtcNow) : "an earlier visit";
}