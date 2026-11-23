using Avalonia.Controls;
using Avalonia.Interactivity;

using Tereditor.Checks.E2E;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.GitHub.UI;
using Tereditor.UI.Plugins;

using static Tereditor.Checks.E2E.E2eWorkspace;

namespace Tereditor.Checks;

internal static partial class GitHubBrowserChecks
{
    /// <summary>
    /// A plugin document's registration adds entries to its tab's context menu, for that plugin's scheme only: hidden by the
    /// predicate, disabled by the other, run off the UI thread with the document's identity. GitHub contributes Open in browser.
    /// </summary>
    internal static void RunTabMenu(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "github-tab-menu", "project"), openFiles: false);
        var registry = app.Workbench.PluginRegistry;
        var ran = new List<(PluginDocumentProps Props, bool OnUi)>();
        var lockObject = new object();
        registry.AddDocument("fixture", new("fixture", _ => new TextBlock { Text = "fixture" })
        {
            MenuEntries =
            [
                new("Go", "Go", props => { lock (lockObject) ran.Add((props, Avalonia.Threading.Dispatcher.UIThread.CheckAccess())); return Task.CompletedTask; }),
                new("OnlyA", "Only A", _ => Task.CompletedTask) { Visible = props => props.Id == "a" },
                new("NotB", "Not B", _ => Task.CompletedTask) { Enabled = props => props.Id != "b" },
                new("Throws", "Throws", _ => Task.CompletedTask) { Visible = _ => throw new InvalidOperationException("fixture") },
            ]
        });
        registry.AddDocument("other", new("other", _ => new TextBlock { Text = "other" }));

        List<Control> Items(string scheme, string id) => app.Window.TabMenuItems(new($"plugin:{scheme}:{id}", id.ToUpperInvariant(), "plugin", $"{scheme}:{id}"));
        static string[] Names(IEnumerable<Control> items) => [.. items.OfType<MenuItem>().Select(item => item.Name ?? "")];

        var a = Items("fixture", "a");
        Require(a.FirstOrDefault() is Separator, "The plugin's entries are a separate group, after a separator");
        Require(Names(a).SequenceEqual(["DocumentMenu_fixture_Go", "DocumentMenu_fixture_OnlyA", "DocumentMenu_fixture_NotB"]), "A document of the scheme gets its entries in order, a throwing predicate hides its entry: " + string.Join(", ", Names(a)));
        Require(a.OfType<MenuItem>().Select(item => (string?)item.Header).SequenceEqual(["Go", "Only A", "Not B"]), "The entries carry their labels");
        var b = Items("fixture", "b");
        Require(Names(b).SequenceEqual(["DocumentMenu_fixture_Go", "DocumentMenu_fixture_NotB"]), "The visible predicate hides an entry for a document: " + string.Join(", ", Names(b)));
        Require(b.OfType<MenuItem>().Single(item => item.Name == "DocumentMenu_fixture_NotB").IsEnabled == false && a.OfType<MenuItem>().Single(item => item.Name == "DocumentMenu_fixture_NotB").IsEnabled, "The enabled predicate disables an entry without hiding it");
        Require(Items("other", "a").Count == 0, "A document of another plugin gets none of the fixture's entries");
        Require(Items("nobody", "a").Count == 0, "A scheme nobody registered gets none");

        a.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Until(() => { lock (lockObject) return ran.Count == 1; });
        Require(ran[0].Props is { Id: "a", Title: "A" } && ran[0].Props.WorkspaceId.Length > 0 && !ran[0].OnUi, "The action got the document's identity and ran off the UI thread");

        var workspace = ran[0].Props.WorkspaceId;
        Require(Names(Items("github", "issues")).Length == 0, "A GitHub tab that has not loaded its page offers no Open in browser");
        GitHubWebLink.Remember(GitHubWebLink.Key(workspace, "issues"), "https://github.com/o/r/issues");
        Require(Names(Items("github", "issues")).SequenceEqual(["DocumentMenu_github_OpenInBrowser"]), "A GitHub tab with its address offers Open in browser");
        Require(Names(Items("github", "pulls")).Length == 0, "Another GitHub tab does not borrow the address");
        GitHubWebLink.Forget(workspace, "issues");
        Console.WriteLine("PASS plugin document tab menus: entries for the plugin's own scheme only, visible and enabled predicates, the action runs off the UI thread with the document's identity, GitHub offers Open in browser");
    }
}