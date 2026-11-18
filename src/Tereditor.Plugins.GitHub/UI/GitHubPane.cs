using Avalonia.Controls;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>The GitHub side tool: only buttons, each opening a tab of the plugin's own document scheme.</summary>
internal sealed class GitHubPane : StackPanel
{
    private readonly IPluginUIContext context;
    private string workspace;

    public GitHubPane(IPluginUIContext context, string workspace)
    {
        this.context = context;
        this.workspace = workspace;
        Name = "GitHubPane";
        Spacing = 6;
        Margin = new Avalonia.Thickness(10);
        Children.Add(Open("Issues", "error-warning-line", GitHubTab.Issues));
        Children.Add(Open("Pull requests", "git-pull-request-line", GitHubTab.Pulls));
        Children.Add(Open("Actions", "play-circle-line", "runs"));
        Children.Add(Open("Discussions", "discuss-line", "discussions"));
        Children.Add(Open("Releases", "price-tag-3-line", "releases"));
        Children.Add(Open("Contributors", "group-line", "contributors"));
        Children.Add(Open("Deployments", "rocket-line", "deployments"));
        Children.Add(Open("Packages", "box-3-line", "packages"));
        Children.Add(Open("Settings", "settings-3-line", GitHubTab.Settings));
    }

    public bool Retarget(string workspaceId) { workspace = workspaceId; return true; }

    private Button Open(string label, string icon, string id)
    {
        var button = Ui.Button(label, () => _ = OpenAsync(label, id), icon);
        button.Name = "GitHub" + id;
        button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        return button;
    }

    private async Task OpenAsync(string title, string id)
    {
        try { await context.OpenDocumentAsync(workspace, GitHubContract.Scheme, id, title); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't open GitHub", error.Message);
        }
    }
}
