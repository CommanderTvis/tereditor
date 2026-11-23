using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>
/// The one "Open in browser" button every GitHub document shows. It opens the github.com page of the same thing through
/// <see cref="GitHubRuns.Open"/> (system browser, off the UI thread, github.com addresses only). The address is in <c>Tag</c>.
/// </summary>
internal static class GitHubWebLink
{
    internal const string ButtonName = "GitHubOpenInBrowser";

    internal static Button Create(string url)
    {
        Button? self = null;
        var button = self = Ui.Button("Open in browser", () => GitHubRuns.Open((string)(self!.Tag ?? "")), GitHubIcons.ExternalLink);
        button.Name = ButtonName;
        ToolTip.SetTip(button, "Open this page on github.com");
        AutomationProperties.SetName(button, "Open in browser");
        Set(button, url);
        return button;
    }

    internal static void Set(Button button, string url)
    {
        button.Tag = url;
        button.IsVisible = url.Length > 0;
    }

    /// <summary>The repository's address from an answer: its own, or the first github.com address in its rows or summary cut to owner and name.</summary>
    internal static string RepoUrl(GitHubView view)
    {
        if (view.RepoUrl.Length > 0) return view.RepoUrl;
        var url = view.Rows.Select(r => r.Url).Concat([view.Summary]).FirstOrDefault(u => u.StartsWith("https://github.com/", StringComparison.Ordinal)) ?? "";
        var parts = url.Split('/');
        return parts.Length > 4 ? string.Join('/', parts[..5]) : "";
    }
}