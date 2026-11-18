using Avalonia.Controls;
using Avalonia.Controls.Primitives;

using Tereditor.Plugins.UI.Kit;
using Tereditor.Plugins.UI.Kit.Markdown;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>Renders GitHub's Markdown (issue bodies, comments, release notes) with the Markdown preview the rest of the app uses.</summary>
internal static class GitHubMarkdown
{
    public static Control Render(string text)
    {
        var preview = new MarkdownPreview(text, "github.md", new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, Ui.FontSize, 100, true));
        // The tab scrolls; the preview must not take the wheel.
        preview.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        return preview;
    }
}
