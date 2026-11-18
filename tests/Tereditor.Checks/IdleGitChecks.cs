using System.Diagnostics;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;

namespace Tereditor.Checks;

/// <summary>
/// An idle host asks Git the same read-only questions again and again (workspace listing, snapshot, branches, branch status,
/// change totals). Over a normal repository, one without commits, a plain folder, a bare repository, a detached HEAD and a
/// linked worktree, every pass after the first raises no exception and starts the same bounded number of Git runs, and
/// reading the host state starts none. Quiet probes that exit non-zero are counted as results, by command, and printed.
/// </summary>
internal static class IdleGitChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(string fixture)
    {
        var root = Path.Combine(fixture, "idle-git");
        var normal = E2E.IsolatedGit.Repository(Path.Combine(root, "normal"));
        var origin = E2E.IsolatedGit.Repository(Path.Combine(root, "origin"));
        // An origin without origin/HEAD, and a branch that was never published: the quiet probes of a real checkout answer "no".
        E2E.IsolatedGit.Run(normal, "remote", "add", "origin", origin);
        E2E.IsolatedGit.Run(normal, "checkout", "-b", "feature");
        var worktree = Path.Combine(root, "linked");
        E2E.IsolatedGit.Run(normal, "worktree", "add", "-b", "linked-branch", worktree);
        var unborn = Path.Combine(root, "unborn");
        Directory.CreateDirectory(unborn);
        E2E.IsolatedGit.Run(unborn, "init", "-b", "main");
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "notes.txt"), "not a repository\n");
        var bare = Path.Combine(root, "bare.git");
        Directory.CreateDirectory(bare);
        E2E.IsolatedGit.Run(bare, "init", "--bare", "-b", "main");
        var detached = E2E.IsolatedGit.Repository(Path.Combine(root, "detached"));
        E2E.IsolatedGit.Run(detached, "checkout", "--detach");
        var folders = new[] { normal, worktree, unborn, plain, bare, detached };
        var services = folders.ToDictionary(folder => folder, folder => new ProjectServices(folder));

        var thrown = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            var frame = new StackTrace(args.Exception, false).GetFrames().Select(item => item.GetMethod())
                .FirstOrDefault(method => method?.DeclaringType?.Namespace?.StartsWith("Tereditor.Host", StringComparison.Ordinal) == true);
            var site = frame is null ? "?" : frame.DeclaringType!.Name + "." + frame.Name;
            thrown.AddOrUpdate(args.Exception.GetType().Name + " at " + site, 1, (_, count) => count + 1);
        }

        // One pass is what the workbench asks after a file or Git change: the listing, the snapshot, and for a repository the
        // branches, the branch status and the totals of each worktree the snapshot lists.
        async Task Pass()
        {
            foreach (var folder in folders)
            {
                var host = services[folder];
                // A linked worktree is no project of its own: the registry lists the main folder.
                if (folder != worktree) await host.ListWorkspacesAsync(folder);
                var snapshot = await host.GetGitAsync("");
                if (!snapshot.IsRepository) continue;
                await host.ListBranchesAsync(false);
                await host.GetBranchStatusAsync(false);
                foreach (var tree in snapshot.Worktrees) await host.GetDiffStatsAsync(tree.Path);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += Count;
        var passes = new List<long>();
        var first = Interlocked.Read(ref GitRepository.Invocations);
        try
        {
            await Pass();
            var firstRaised = Interlocked.Read(ref GitRepository.Raised);
            var firstThrown = thrown.Values.Sum();
            passes.Add(Interlocked.Read(ref GitRepository.Invocations) - first);
            foreach (var (command, count) in GitRepository.NonZeroExits.OrderByDescending(pair => pair.Value))
                Console.WriteLine($"idle-git: first pass, non-zero exit answered as a result, {count}: {command}");
            foreach (var (site, count) in thrown.OrderByDescending(pair => pair.Value))
                Console.WriteLine($"idle-git: first pass, exception, {count}: {site}");
            GitRepository.NonZeroExits.Clear();
            thrown.Clear();
            var raised = Interlocked.Read(ref GitRepository.Raised);
            for (var round = 0; round < 10; round++)
            {
                var before = Interlocked.Read(ref GitRepository.Invocations);
                await Pass();
                passes.Add(Interlocked.Read(ref GitRepository.Invocations) - before);
            }
            Console.WriteLine($"idle-git: first pass {passes[0]} Git runs, {firstThrown} exceptions, {firstRaised} Git failures raised");
            Console.WriteLine($"idle-git: later passes {string.Join(", ", passes.Skip(1))} Git runs each");
            foreach (var (command, count) in GitRepository.NonZeroExits.OrderByDescending(pair => pair.Value))
                Console.WriteLine($"idle-git: non-zero exit answered as a result, {count} in 10 passes: {command}");
            foreach (var (site, count) in thrown.OrderByDescending(pair => pair.Value))
                Console.WriteLine($"idle-git: exception, {count} in 10 passes: {site}");
            Require(thrown.IsEmpty, "An idle host must throw nothing after its first refresh: " + string.Join("; ", thrown.Select(pair => pair.Value + " x " + pair.Key)));
            Require(Interlocked.Read(ref GitRepository.Raised) == raised, "An idle host must raise no Git failure after its first refresh.");
            Require(passes.Skip(1).Distinct().Count() == 1, "Every later pass starts the same Git runs: " + string.Join(", ", passes));
            Require(passes[1] <= 30 * folders.Length, $"A pass over {folders.Length} folders starts a bounded number of Git runs, not {passes[1]}.");
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Count; }

        // Reading the host state is a memory read.
        var store = new HostStateStore(Path.Combine(root, "state"));
        await store.ChangeAsync([HostStateChange.OpenProject(normal)]);
        var runs = Interlocked.Read(ref GitRepository.Invocations);
        for (var round = 0; round < 300; round++) _ = store.Current;
        Require(Interlocked.Read(ref GitRepository.Invocations) == runs, "Reading the host state must start no Git run.");
        Console.WriteLine("OK idle git");
    }
}