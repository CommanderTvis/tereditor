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

    /// <summary>
    /// Proves the UI thread keeps serving its queue: a probe posted at Background priority must run. A thread that never returns cannot
    /// report itself, so a second thread names the step that stopped answering and ends the process.
    /// </summary>
    private sealed class Watchdog : IDisposable
    {
        private const int Seconds = 20;
        private volatile bool answered = true, stopped;
        private volatile string step = "";
        private long since;

        internal Watchdog() => new Thread(() =>
        {
            while (!stopped)
            {
                Thread.Sleep(500);
                if (!answered && Environment.TickCount64 - Interlocked.Read(ref since) > Seconds * 1000)
                    Environment.FailFast($"The UI thread stopped serving its queue for {Seconds} s after: {step}");
            }
        })
        { IsBackground = true, Name = "GitHubTabCloseWatchdog" }.Start();

        internal void Probe(string after)
        {
            step = after; answered = false; Interlocked.Exchange(ref since, Environment.TickCount64);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => answered = true, Avalonia.Threading.DispatcherPriority.Background);
            Until(() => answered, "the UI thread to run a Background probe after " + after);
        }

        public void Dispose() => stopped = true;
    }

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
        using var watchdog = new Watchdog();
        // Open, close, open, close, open, close: the button, then the middle click, then the button again.
        foreach (var middle in new[] { false, true, false })
            foreach (var id in Ids)
            {
                Until(() => app.Window.WorkspaceMounted);
                _ = app.Window.OpenPluginDocumentAsync(workspace, "github", id, id);
                var name = "DockTab_plugin_github_" + id.Replace(':', '_');
                Until(() => Controls(app).Any(control => control.Name == name && control.IsEffectivelyVisible));
                Settle(400);
                watchdog.Probe("opened " + id);
                if (id == "issues")
                {
                    var tab = Controls(app).OfType<Border>().Single(control => control.Name == "GitHubTab");
                    var item = new GitHubItem(1, "Title", "open", false, "ann", [], 0, "2026-11-01T10:00:00Z", "2026-11-02T10:00:00Z", null, "https://github.com/o/r/issues/1", "none", "none")
                    { Assignees = [new("ann", avatarUrl)] };
                    var list = new GitHubList(false, "r", "o/r", [item], 1, 0, false);
                    // A refreshed answer draws the page again; the search box lives on and must move to the new header.
                    ((GitHubTab)tab).RenderList(list);
                    ((GitHubTab)tab).RenderList(list with { Cached = new(true, "2026-11-01T10:00:00Z") });
                    ((GitHubTab)tab).RenderList(list);
                    Require(Controls(app).OfType<TextBox>().Count(box => box.Name == "GitHubQuery") == 1, "A redrawn Issues list must keep exactly one search box.");
                    watchdog.Probe("redrawn " + id);
                    Until(() => tab.GetLogicalDescendants().OfType<Image>().Any(image => ReferenceEquals(image.Source, avatar)));
                }
                var chrome = Controls(app).First(control => control.Name == name && control.IsEffectivelyVisible);
                if (middle) app.Click(chrome, mouseButton: MouseButton.Middle);
                else app.Click(chrome.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CloseTab"));
                Settle(1500);
                Until(() => !Controls(app).Any(control => control.Name == name));
                watchdog.Probe("closed " + id);
                Require(app.State!.Current.Projects.Contains(project) && app.State.Current.Workspaces.Any(item => item.Path == workspace),
                    $"Closing the {id} GitHub tab must keep the project and its workspace in the host state.");
                Require(Controls(app).Any(control => control.Name == "ProjectName" && Equals(control.Tag, project)),
                    $"Closing the {id} GitHub tab must keep the project in the rail.");
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