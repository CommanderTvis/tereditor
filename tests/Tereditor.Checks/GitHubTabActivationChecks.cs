using System.Reflection;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.UI;

namespace Tereditor.Checks;

/// <summary>A plugin UI context that answers GitHub detail requests from one canned conversation, by revision, and records them.</summary>
public class GitHubFakeContext : DispatchProxy
{
    public GitHubDetail? Detail { get; set; }
    public int Requests;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) return null;
        if (method.Name == "Settings") return Activator.CreateInstance(method.GetGenericArguments()[0]);
        if (method.Name == "RequestAsync" && args is [_, GitHubDetailParams query, ..])
        {
            Interlocked.Increment(ref Requests);
            var answer = query.Revision == Detail!.Revision ? Detail with { Unchanged = true } : Detail;
            return Activator.CreateInstance(method.ReturnType, answer);
        }
        return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
    }
}

internal static partial class GitHubBrowserChecks
{
    /// <summary>Every kind of GitHub document has the one Open in browser button, with the github.com address of the same thing.</summary>
    private static void OpenInBrowserLinks()
    {
        static void Has(Control tab, string url, string what, string id)
        {
            Require(GitHubWebLink.Known("w", id) == url, $"{what} tells its tab menu the same address {url}, got {GitHubWebLink.Known("w", id)}");
            var open = Named(tab, GitHubWebLink.ButtonName) as Button;
            Require(open is { IsVisible: true } && (string?)open.Tag == url && ToolTip.GetTip(open) is string { Length: > 0 } && AutomationProperties.GetName(open) == "Open in browser", $"{what} offers Open in browser to {url}, got {open?.Tag} (visible {open?.IsVisible})");
        }
        var timeline = new List<GitHubEvent> { new("comment", "bob", "2026-11-01T11:00:00Z", "Text", "") };
        foreach (var (kind, path) in new[] { (GitHubTab.Issues, "issues"), (GitHubTab.Pulls, "pull") })
        {
            var tab = new GitHubTab(null!, "w", kind);
            tab.RenderDetail(new(false, "r", "o/r", kind, 7, "T", "open", false, "ann", "2026-11-01T10:00:00Z", $"https://github.com/o/r/{path}/7", "Body", [], [], null, timeline, false, null));
            Has(tab, $"https://github.com/o/r/{path}/7", "A " + kind + " conversation", kind);
            tab.RenderList(new(false, "r", "o/r", [], 0, 0, false));
            Has(tab, "https://github.com/o/r/" + (kind == GitHubTab.Pulls ? "pulls" : "issues"), "The " + kind + " list", kind);
        }
        var rows = new List<GitHubRow> { new("1", "Row", "open", "meta", [], "right", "https://github.com/o/r/discussions/1") };
        foreach (var (id, path) in new[] { ("discussions", "discussions"), ("releases", "releases"), ("deployments", "deployments"), ("packages", "packages"), ("discussion:4", "discussions/4") })
        {
            var tab = new GitHubViewTab(null!, "w", id);
            tab.Render(new(false, "r", "Title", "Sub", "none", rows, [], [], false, "https://github.com/o/r/"));
            Has(tab, "https://github.com/o/r/" + path, "The " + id + " view", id);
        }
        var contributors = new GitHubContributorsTab(null, "w", (_, _) => Task.FromResult(new GitHubContributors(true, "", "", false, [], [])));
        contributors.Show(new GitHubContributors(false, "r", "main", false, [1_700_000_000L], [new("ann", "", "https://github.com/ann", [1], [1], [1])]) { RepoUrl = "https://github.com/o/r" });
        var window = Show(contributors);
        Has(contributors, "https://github.com/o/r/graphs/contributors", "The contributors page", "contributors");
        window.Close();
        Console.WriteLine("PASS every GitHub document kind has an Open in browser button with its github.com address");
    }

    /// <summary>Showing a loaded issue tab again keeps its controls: no loading text, no rebuild, no flicker.</summary>
    private static void ReactivatedTabKeepsItsView()
    {
        var context = DispatchProxy.Create<IPluginUIContext, GitHubFakeContext>();
        var fake = (GitHubFakeContext)(object)context;
        fake.Detail = new(false, "rev1", "o/r", GitHubTab.Issues, 7, "Add b", "open", false, "ann", "2026-11-01T10:00:00Z", "https://github.com/o/r/issues/7", "Body", [], ["bob"], null,
            [new("comment", "bob", "2026-11-01T11:00:00Z", "Text", "", "", [])], false, null);
        var tab = new GitHubTab(context, "w", "issue:7");
        var window = Show(tab);
        Control? Conversation() => Named(tab, "GitHubConversation");
        static void Pump(Func<bool> done)
        {
            for (var i = 0; i < 500 && !done(); i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        }
        Pump(() => Conversation() is not null);
        Require(Conversation() is not null, "The issue tab loads its conversation");
        var first = Conversation()!;
        var composer = Named(tab, "GitHubComposer");
        var scroll = tab.GetLogicalDescendants().OfType<ScrollViewer>().First();
        var changes = 0;
        scroll.PropertyChanged += (_, e) => { if (e.Property == ContentControl.ContentProperty) changes++; };

        for (var round = 1; round <= 2; round++)
        {
            var before = Volatile.Read(ref fake.Requests);
            window.Content = null; Frame(window);
            window.Content = tab; Frame(window);
            Pump(() => Volatile.Read(ref fake.Requests) > before);
            Thread.Sleep(50); Frame(window);
            Require(Volatile.Read(ref fake.Requests) > before, "Showing the tab again asks whether anything changed, round " + round);
            Require(ReferenceEquals(Conversation(), first) && ReferenceEquals(Named(tab, "GitHubComposer"), composer), "Showing the tab again keeps the same conversation and comment box, round " + round);
            Require(changes == 0, "Showing the tab again never replaces or clears its content, round " + round);
        }
        window.Close();
        Console.WriteLine("PASS GitHub issue tab keeps its view when it is shown again");
    }
}