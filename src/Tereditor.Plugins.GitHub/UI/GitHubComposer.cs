using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// The comment box at the end of a conversation, in GitHub's layout: Write and Preview tabs, Comment, and Close or Reopen. Nothing is
/// sent except by a click (or Ctrl/Cmd+Enter) on a button here. The draft lives in <see cref="GitHubDrafts"/>, so a refresh that
/// rebuilds the view keeps what was typed.
/// </summary>
internal sealed class GitHubComposer : Border
{
    private readonly string key;
    private readonly Func<string, string, Task<GitHubPostResult>> post;
    private readonly Action<GitHubEvent?, string?> posted;
    private readonly GitHubLinker? linker;
    private readonly TextBox box = new() { Name = "GitHubCommentBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, PlaceholderText = "Leave a comment" };
    private readonly Border preview = new() { MinHeight = 110, Padding = new Thickness(8), IsVisible = false };
    private readonly Button write, view, comment;
    private readonly Button? state;
    private readonly TextBlock status = Ui.Text("", Ui.Muted, 12);
    private bool posting;
    private string current = "open";

    public GitHubComposer(string key, bool canChangeState, Func<string, string, Task<GitHubPostResult>> post, Action<GitHubEvent?, string?> posted, GitHubLinker? linker)
    {
        this.key = key; this.post = post; this.posted = posted; this.linker = linker;
        Name = "GitHubComposer";
        status.TextWrapping = TextWrapping.Wrap; status.TextTrimming = TextTrimming.None;
        write = Ui.Button("Write", () => Show(false)); write.Name = "GitHubWriteTab";
        view = Ui.Button("Preview", () => Show(true)); view.Name = "GitHubPreviewTab";
        comment = Ui.Button("Comment", () => _ = SubmitAsync("comment")); comment.Name = "GitHubComment";
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 8, 8, 0) };
        tabs.Children.Add(write); tabs.Children.Add(view);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (canChangeState) { state = Ui.Button("Close", () => _ = SubmitAsync(current == "open" ? "close" : "reopen")); state.Name = "GitHubState"; buttons.Children.Add(state); }
        buttons.Children.Add(comment);
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(8) };
        body.Children.Add(box); body.Children.Add(preview); body.Children.Add(status); body.Children.Add(buttons);
        var frame = new StackPanel();
        frame.Children.Add(new Border { Background = Ui.Elevated, Child = tabs, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1) });
        frame.Children.Add(body);
        Child = frame;
        BorderBrush = Ui.BorderBrush; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6); ClipToBounds = true;
        box.Text = GitHubDrafts.Get(key);
        box.TextChanged += (_, _) => { GitHubDrafts.Set(key, box.Text ?? ""); Update(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { e.Handled = true; _ = SubmitAsync("comment"); }
        };
        Update();
    }

    /// <summary>Takes the composer out of its previous parent so a rebuilt view can hold it.</summary>
    public void Detach() => (Parent as Panel)?.Children.Remove(this);

    public void SetState(string value)
    {
        current = value;
        Update();
    }

    private void Show(bool previewing)
    {
        box.IsVisible = !previewing; preview.IsVisible = previewing;
        if (previewing) preview.Child = (box.Text ?? "").Trim().Length == 0 ? Ui.Text("Nothing to preview") : GitHubMarkdown.Render(box.Text!, linker);
    }

    private void Update()
    {
        var has = (box.Text ?? "").Trim().Length > 0;
        comment.IsEnabled = has && !posting;
        if (state is not null)
        {
            state.IsEnabled = !posting;
            state.Content = Ui.Text(current == "open" ? (has ? "Close with comment" : "Close") : (has ? "Reopen with comment" : "Reopen"));
        }
    }

    private async Task SubmitAsync(string action)
    {
        var text = box.Text ?? "";
        if (posting || action == "comment" && text.Trim().Length == 0) return;
        posting = true; Update();
        status.Foreground = Ui.Muted; status.Text = "Posting…";
        GitHubPostResult result;
        try { result = await post(action, text); }
        catch (Exception error) when (error is not OperationCanceledException) { result = new(false, error.Message); }
        posting = false;
        if (result.Event is not null) { box.Text = ""; GitHubDrafts.Set(key, ""); }
        if (result.Ok) status.Text = "";
        else { status.Foreground = Ui.Danger; status.Text = result.Problem ?? "gh could not post."; }
        Update();
        if (result.Event is not null || result.State is not null) posted(result.Event, result.State);
    }
}