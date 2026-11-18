using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;

namespace Tereditor.Checks;

/// <summary>
/// A project or workspace whose folder is gone is a known state: after the first detection, listing it, asking for its
/// totals and reading the host state start no Git run, look at no folder again within the back-off and throw nothing.
/// </summary>
internal static class MissingProjectChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(string fixture)
    {
        var root = Path.Combine(fixture, "missing-projects");
        var repository = E2E.IsolatedGit.Repository(Path.Combine(root, "repo"));
        var store = new HostStateStore(Path.Combine(root, "state"));
        var service = new ProjectServices(repository, store);
        await store.ChangeAsync([HostStateChange.OpenProject(repository)]);
        Require((await service.ListWorkspacesAsync(repository)).Workspaces.Count == 1, "A present project lists its workspace.");
        Require(GitRepository.Invocations > 0, "The listing of a present project asks Git, so the counter sees it.");

        Directory.Delete(repository, recursive: true);
        // The first detection may look and broadcast once.
        Require((await service.ListWorkspacesAsync(repository)).Workspaces.Count == 0, "A missing project lists no workspaces.");
        Require(!store.Current.Projects.Contains(repository), "A missing project leaves the visible state.");

        var thrown = 0;
        void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args) => Interlocked.Increment(ref thrown);
        var git = Interlocked.Read(ref GitRepository.Invocations);
        var probes = Interlocked.Read(ref FolderProbe.Probes);
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            for (var round = 0; round < 300; round++)
            {
                Require((await service.ListWorkspacesAsync(repository)).Workspaces.Count == 0, "A missing project keeps listing no workspaces.");
                Require(await service.GetDiffStatsAsync(Path.Combine(repository, "gone")) is null, "A missing worktree has no totals.");
                _ = store.Current;
            }
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Count; }
        Require(Interlocked.Read(ref GitRepository.Invocations) == git, "A missing project must start no Git run after its first detection.");
        Require(Volatile.Read(ref thrown) == 0, $"A missing project must throw nothing; {thrown} exceptions were thrown.");
        Require(Interlocked.Read(ref FolderProbe.Probes) - probes <= 4, "A missing project is looked at again only after its back-off.");

        Directory.CreateDirectory(repository);
        Require(FolderProbe.Exists(repository, fresh: true), "A folder that comes back is found by a fresh look.");
        Console.WriteLine("OK missing projects");
    }
}