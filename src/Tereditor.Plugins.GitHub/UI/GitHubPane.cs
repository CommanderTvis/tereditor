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
        Children.Add(Open("Issues", GitHubIcons.AlertImportant, GitHubTab.Issues));
        Children.Add(Open("Pull requests", GitHubIcons.GitPullRequest, GitHubTab.Pulls));
        Children.Add(Open("Actions", GitHubIcons.PlayCircle, "runs"));
        Children.Add(Open("Discussions", GitHubIcons.Discuss, "discussions"));
        Children.Add(Open("Releases", GitHubIcons.PriceTag, "releases"));
        Children.Add(Open("Contributors", GitHubIcons.Group, "contributors"));
        Children.Add(Open("Deployments", GitHubIcons.Rocket, "deployments"));
        Children.Add(Open("Packages", GitHubIcons.Box, "packages"));
        Children.Add(Open("Settings", GitHubIcons.Settings, GitHubTab.Settings));
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