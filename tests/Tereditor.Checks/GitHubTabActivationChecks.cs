using System.Reflection;

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
