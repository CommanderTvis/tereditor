using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.GitHub.UI;

/// <summary>The UI half: the Review panel section (W28), and the Settings page (W4).</summary>
public sealed class GitHubUI : PluginUIModule
{
    public const string Provider = GitHubContract.Id;

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SideTool(new(GitHubContract.Tool, workspace => new GitHubPane(context, workspace)) { Retarget = (control, workspace) => ((GitHubPane)control).Retarget(workspace) });
        context.Document(new PluginDocumentRegistration(GitHubContract.Scheme, props => GitHubTab.Handles(props.Id) ? new GitHubTab(context, props.WorkspaceId, props.Id) : props.Id == "contributors" ? new GitHubContributorsTab(context, props.WorkspaceId) : props.Id == "runs" ? new GitHubActionsTab(context, props.WorkspaceId) : props.Id.StartsWith("run:", StringComparison.Ordinal) ? new GitHubRunTab(context, props.WorkspaceId, props.Id) : new GitHubViewTab(context, props.WorkspaceId, props.Id)) { Icon = "asset:github.svg" });
        context.RepoHostUI(new(surface => new GitHubReviewSection(surface)) { OpenLink = (workspace, url) => OpenLinkAsync(context, workspace, url) });
        context.SettingsSection(new("GitHub", "asset:github.svg", () => new GitHubSettingsPage(context)));
        context.KeymapAction(new("OpenSettings", "Open GitHub settings", "GitHub", () => context.OpenSettings(UiPages.PluginSection(context.Id))));
        return null;
    }

    /// <summary>Opens a link to a run, issue, pull request, discussion or the releases of the repository the link names in this plugin's own tab; false for any other link.</summary>
    internal static async ValueTask<bool> OpenLinkAsync(IPluginUIContext context, string workspace, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Segments.Length < 3 || GitHubLinks.Parse(url, uri.Segments[1].Trim('/') + "/" + uri.Segments[2].Trim('/')) is not { } target) return false;
        await context.OpenDocumentAsync(workspace, GitHubContract.Scheme, target.Id, target.Title);
        return true;
    }
}