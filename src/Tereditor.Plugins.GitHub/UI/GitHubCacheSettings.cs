using Avalonia.Controls;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>The cache switch and the Clear cache button with its size, shown on the GitHub settings page and tab.</summary>
internal sealed class GitHubCacheSettings : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly TextBlock size = Ui.Text("", Ui.Muted, 13);

    public GitHubCacheSettings(IPluginUIContext context)
    {
        this.context = context;
        Name = "GitHubCacheSettings";
        Spacing = 8;
        var keep = new CheckBox { Name = "GitHubKeepCache", Content = "Keep a local cache of GitHub data", IsChecked = context.Settings<GitHubSettings>().KeepCache };
        keep.IsCheckedChanged += async (_, _) =>
        {
            try { await context.UpdateSettingsAsync(context.Settings<GitHubSettings>() with { KeepCache = keep.IsChecked == true }); }
            catch (Exception error) when (error is not OperationCanceledException) { }
        };
        var note = Ui.Text("A tab opens with the last data at once and refreshes it. The copy stays in your user folder, readable only by you. It holds issue and discussion text, never credentials.", Ui.Muted, 13);
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var clear = Ui.Button("Clear cache", () => _ = AskAsync(true));
        clear.Name = "GitHubClearCache";
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(clear);
        row.Children.Add(size);
        Children.Add(keep);
        Children.Add(note);
        Children.Add(row);
        AttachedToVisualTree += async (_, _) => await AskAsync(false);
    }

    private async Task AskAsync(bool clear)
    {
        long bytes;
        try { bytes = (await Task.Run(async () => await context.RequestAsync(GitHubContract.Cache, new GitHubCacheQuery(clear), CancellationToken.None).AsTask())).Bytes; }
        catch (Exception error) when (error is not OperationCanceledException) { size.Text = "The size is not known."; return; }
        size.Text = clear ? "Cleared " + Format(bytes) + "." : "Uses " + Format(bytes) + ".";
    }

    internal static string Format(long bytes) => bytes >= 1 << 20 ? (bytes / (double)(1 << 20)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : bytes >= 1024 ? (bytes / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KB" : bytes + " bytes";
}