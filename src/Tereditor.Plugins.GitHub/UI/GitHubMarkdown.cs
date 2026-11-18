using Avalonia.Controls;
using Avalonia.Controls.Primitives;

using Tereditor.Plugins.UI.Kit;
using Tereditor.Plugins.UI.Kit.Markdown;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>What a rendered body needs from its tab: the repository (for #N references) and a way to open a link in the app.</summary>
internal sealed class GitHubLinker(Func<string> repo, Func<string, bool> open)
{
    public string Repo => repo();
    public bool Open(string url) => open(url);
}

/// <summary>
/// Renders GitHub's Markdown (issue bodies, comments, release notes) with the Markdown preview the rest of the app uses. Links to this
/// repository open as tabs; other http and https links open in the system browser, as the preview does. Images are fetched by the preview off the
/// UI thread (http and https only, 8 MB at most, no credentials sent) and show their alt text when they cannot load.
/// </summary>
internal static class GitHubMarkdown
{
    public static Control Render(string text, GitHubLinker? linker = null)
    {
        var shown = linker is null ? text : GitHubLinks.Rewrite(text, linker.Repo);
        var preview = new MarkdownPreview(shown, "github.md", new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, Ui.FontSize, 100, true)
        {
            OpenLink = url => linker?.Open(url) ?? false
        });
        // The tab scrolls; the preview must not take the wheel.
        preview.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        return preview;
    }
}