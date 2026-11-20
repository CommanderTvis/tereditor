using Avalonia.Controls;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI.Panels;

/// <summary>
/// Says why a language server cannot start and lets the user fix it on the spot: choose the server as the Java and Kotlin provider,
/// and review and accept its license terms. One component for the language pane and the server's configuration window.
/// </summary>
internal sealed class LanguageBlockedNotice : StackPanel
{
    private readonly TextBlock message = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Ui.Danger };
    private readonly Button useProvider = new() { Margin = new(0, 0, 8, 0) };
    private readonly Button reviewTerms = new() { Content = "Review and accept terms", Margin = new(0, 0, 8, 0) };
    private readonly TextBlock failure = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Ui.Danger, IsVisible = false };
    private IProjectServices? host;
    private Func<string> workspace = () => "";
    private string server = "";
    private Func<bool, Task>? start;
    private Window? terms;

    public LanguageBlockedNotice()
    {
        Spacing = 4;
        IsVisible = false;
        Name = "BlockedNotice";
        useProvider.Name = "BlockedUseProvider";
        reviewTerms.Name = "BlockedReviewTerms";
        message.Name = "BlockedMessage";
        var buttons = new WrapPanel();
        buttons.Children.Add(useProvider);
        buttons.Children.Add(reviewTerms);
        Children.Add(message);
        Children.Add(buttons);
        Children.Add(failure);
        useProvider.Click += async (_, _) => await UseProviderAsync();
        reviewTerms.Click += (_, _) => ReviewTerms();
    }

    /// <summary>The terms window this notice opened, while it is open.</summary>
    internal Window? TermsWindow => terms;

    internal void Attach(IProjectServices host, Func<string> workspace) { this.host = host; this.workspace = workspace; }

    internal void Hide() { IsVisible = false; failure.IsVisible = false; }

    /// <summary>
    /// Shows what blocks <paramref name="id"/>. <paramref name="start"/> starts the server again; its argument says the terms were just reviewed,
    /// so the start does not ask again.
    /// </summary>
    internal void Show(string id, bool needsProvider, bool needsTerms, Func<bool, Task> start)
    {
        server = id; this.start = start;
        var name = LanguageServerNameConverter.Display(id);
        message.Text = needsProvider ? $"{name} is not your Java and Kotlin provider yet." : $"{name} needs its license terms accepted before it starts.";
        useProvider.Content = "Use " + name;
        useProvider.IsVisible = needsProvider;
        reviewTerms.IsVisible = needsTerms;
        failure.IsVisible = false;
        IsVisible = true;
    }

    private async Task UseProviderAsync()
    {
        if (host is not { } service || start is not { } again) return;
        var id = server;
        useProvider.IsEnabled = false;
        try
        {
            var saved = await Task.Run(async () => await service.LanguageAsync(new(workspace(), Operation: "setJavaKotlinProvider", Configuration: id)));
            if (saved.State is "error" or "unavailable") { Fail(saved.Detail); return; }
            Hide();
            await again(false);
        }
        catch (Exception error) { Fail(error.Message); }
        finally { useProvider.IsEnabled = true; }
    }

    private void ReviewTerms()
    {
        if (host is not { } service || start is not { } again || TopLevel.GetTopLevel(this) is not Window owner) return;
        var id = server;
        var dialog = new LanguageTermsWindow(service, workspace(), id, startAfterAcceptance: true);
        terms = dialog;
        dialog.Closed += async (_, _) =>
        {
            terms = null;
            if (!dialog.StartRequested) return;
            Hide();
            await again(true);
        };
        dialog.Show(owner);
    }

    private void Fail(string text) { failure.Text = text; failure.IsVisible = true; }
}