using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.UI;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

namespace Tereditor.Checks.E2E;

/// <summary>Closing any GitHub tab, by its button or the middle click, leaves the project and its workspace in the rail and the window working.</summary>
internal static class GitHubTabCloseE2E
{
    private static readonly string[] Ids = ["issues", "pulls", "runs", "discussions", "contributors", "releases", "issue:7", "pull:7"];

    private static bool Disposed(Bitmap bitmap)
    {
        try { _ = bitmap.PixelSize; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "github-tab-close"));
        app.ShowTool("projects");
        var project = app.Window.ProjectRoot;
        var workspace = app.Window.WorkspaceRoot;
        // The avatar cache is shared by every GitHub tab, so a tab's close must leave its images alone.
        const string avatarUrl = "https://avatars.example/ann.png";
        var avatar = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        GitHubAvatars.Remember(avatarUrl, avatar);
        foreach (var middle in new[] { false, true })
            foreach (var id in Ids)
            {
                Until(() => app.Window.WorkspaceMounted);
                _ = app.Window.OpenPluginDocumentAsync(workspace, "github", id, id);
                var name = "DockTab_plugin_github_" + id.Replace(':', '_');
                Until(() => Controls(app).Any(control => control.Name == name && control.IsEffectivelyVisible));
                Settle(400);
                if (id == "issues")
                {
                    var tab = Controls(app).OfType<Border>().Single(control => control.Name == "GitHubTab");
                    var item = new GitHubItem(1, "Title", "open", false, "ann", [], 0, "2026-11-01T10:00:00Z", "2026-11-02T10:00:00Z", null, "https://github.com/o/r/issues/1", "none", "none")
                    { Assignees = [new("ann", avatarUrl)] };
                    ((GitHubTab)tab).RenderList(new(false, "r", "o/r", [item], 1, 0, false));
                    Until(() => tab.GetLogicalDescendants().OfType<Image>().Any(image => ReferenceEquals(image.Source, avatar)));
                }
                var chrome = Controls(app).First(control => control.Name == name && control.IsEffectivelyVisible);
                if (middle) app.Click(chrome, mouseButton: MouseButton.Middle);
                else app.Click(chrome.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CloseTab"));
                Settle(1500);
                Until(() => !Controls(app).Any(control => control.Name == name));
                Require(app.State!.Current.Projects.Contains(project) && app.State.Current.Workspaces.Any(item => item.Path == workspace),
                    $"Closing the {id} GitHub tab must keep the project and its workspace in the host state.");
                Require(Controls(app).Any(control => control.Name == "ProjectName" && Equals(control.Tag, project)) &&
                    Controls(app).Any(control => control.Name == "WorkspaceItem" && Equals(control.Tag, workspace)),
                    $"Closing the {id} GitHub tab must keep the project and its workspace in the rail.");
                Require(app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == workspace, $"Closing the {id} GitHub tab must leave the workspace mounted.");
            }
        Require(!Disposed(avatar), "Closing a GitHub tab must not dispose the avatar image the plugin keeps for its other tabs.");
        // Reopening shows the cached avatar again, drawn without an error.
        _ = app.Window.OpenPluginDocumentAsync(workspace, "github", "issues", "issues");
        Until(() => Controls(app).Any(control => control.Name == "GitHubTab"));
        Settle(400);
        var reopened = Controls(app).OfType<Border>().Single(control => control.Name == "GitHubTab");
        ((GitHubTab)reopened).RenderList(new(false, "r", "o/r", [new GitHubItem(1, "Title", "open", false, "ann", [], 0, "2026-11-01T10:00:00Z", "2026-11-02T10:00:00Z", null, "https://github.com/o/r/issues/1", "none", "none") { Assignees = [new("ann", avatarUrl)] }], 1, 0, false));
        Until(() => reopened.GetLogicalDescendants().OfType<Image>().Any(image => ReferenceEquals(image.Source, avatar)));
        Settle(400);
        Require(!Disposed(avatar), "A reopened Issues tab must draw the cached avatar.");
        Console.WriteLine("PASS closing a GitHub tab keeps the project and workspace in the rail");
    }
}
