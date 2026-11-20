using Avalonia.Controls;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>A job that is running, in a few lines: what it does, a bar that has no end and a Cancel button.</summary>
public sealed class DownloadStrip : StackPanel
{
    private readonly TextBlock text = new() { Name = "DownloadText", TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 13 };

    public DownloadStrip(string message, Action cancel)
    {
        Name = "DownloadStrip";
        Spacing = 8;
        text.Foreground = Ui.TextBrush;
        text.Text = message;
        Children.Add(text);
        Children.Add(new ProgressBar { Name = "DownloadProgress", IsIndeterminate = true });
        var button = Ui.Button("Cancel", cancel);
        button.Name = "DownloadCancel";
        Children.Add(button);
    }

    /// <summary>What the job says it is doing now.</summary>
    public string Message { get => text.Text ?? ""; set => text.Text = value; }
}