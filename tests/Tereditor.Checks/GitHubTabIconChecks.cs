using Avalonia.Controls;
using Avalonia.LogicalTree;

using Tereditor.Checks.E2E;
using Tereditor.UI.Plugins;

using static Tereditor.Checks.E2E.E2eWorkspace;

namespace Tereditor.Checks;

internal static partial class GitHubBrowserChecks
{
    /// <summary>
    /// A GitHub document's tab wears the GitHub icon, also a tab restored at startup, which is drawn before the plugin
    /// registers its scheme: registering the scheme redraws the strip.
    /// </summary>
    internal static void RunTabIcon(string root)
    {
        var read = PluginIcons.ReadAsset;
        PluginIcons.ReadAsset = (_, _, _) => Task.FromResult<byte[]?>(null);
        try
        {
            using var app = new E2eWorkspace(Path.Combine(root, "github-tab-icon", "project"), openFiles: false);
            var registry = app.Workbench.PluginRegistry;
            var document = registry.Documents.Single(row => row.Value.Scheme == "github").Value;
            Require(document.Icon == "asset:github.svg", "The GitHub plugin registers its document scheme with the GitHub icon.");
            registry.RemovePlugin("github");
            app.Window.Layout.Open(new("plugin:github:issues", "Issues", "plugin", "github:issues"), keep: true);
            bool Drawn() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "DockTab_plugin_github_issues");
            bool Marked() => app.Window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "DockTab_plugin_github_issues")
                .GetLogicalDescendants().OfType<Control>().Any(control => control.Tag as string == "asset:github.svg");
            Until(Drawn);
            Require(!Marked(), "A document tab restored before its plugin registered has no plugin icon yet.");

            registry.AddDocument("github", document);
            Until(Marked, "the GitHub document's tab shows the GitHub icon once its scheme is registered");
            Console.WriteLine("PASS GitHub document tabs show the GitHub icon, also when restored before the plugin registers");
        }
        finally { PluginIcons.ReadAsset = read; }
    }
}