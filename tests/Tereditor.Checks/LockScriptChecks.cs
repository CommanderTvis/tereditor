namespace Tereditor.Checks;

/// <summary>
/// Tasks.AcquireLock (scripts/lib/Tasks.cs) is the directory lock of the native build recipes: it must remove a lock whose owner is gone
/// instead of waiting for it forever, wait for a live holder, and release its own lock when disposed.
/// </summary>
internal static class LockScriptChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        if (BoundaryChecks.Checkout() is null) { Console.WriteLine("SKIP lock-script: no source checkout"); return; }
        var work = CheckRoot.Create("lock-script");

        // A lock left by a process that no longer exists is removed and taken over.
        var dead = Path.Combine(work, "dead");
        Directory.CreateDirectory(dead);
        File.WriteAllText(Path.Combine(dead, "pid"), "999999");
        var (disposable, output) = Acquire(dead);
        Require(output.Contains("Removing the stale lock"), "A lock whose owner is gone must be taken over: " + output);
        Require(File.ReadAllText(Path.Combine(dead, "pid")).Trim() == Environment.ProcessId.ToString(), "The new holder must record its PID.");
        disposable.Dispose();
        Require(!Directory.Exists(dead), "The lock must be released when disposed.");

        // A lock directory without an owner file is stale once it is two minutes old, and not before.
        var old = Path.Combine(work, "old");
        Directory.CreateDirectory(old);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-10));
        (disposable, output) = Acquire(old);
        Require(output.Contains("names no owner"), "An old lock without an owner file must be taken over: " + output);
        disposable.Dispose();

        // A live holder is waited for, not displaced.
        var live = Path.Combine(work, "live");
        var holder = Tasks.AcquireLock(live);
        Require(File.Exists(Path.Combine(live, "pid")), "The first holder must hold the lock.");
        var started = DateTime.UtcNow;
        var waiter = Task.Run(() => Acquire(live));
        Thread.Sleep(2500);
        Require(!waiter.IsCompleted, "A live holder must be waited for.");
        holder.Dispose();
        var (second, waited) = waiter.GetAwaiter().GetResult();
        Require(DateTime.UtcNow - started > TimeSpan.FromSeconds(1) && !waited.Contains("Removing"), "A live holder must be waited for: " + waited);
        second.Dispose();
        Console.WriteLine("PASS lock: stale locks are taken over, live holders are waited for, the lock is released when disposed");
    }

    /// <summary>Takes the lock and returns what it wrote to standard error while doing so.</summary>
    private static (IDisposable Lock, string Output) Acquire(string directory)
    {
        var error = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { return (Tasks.AcquireLock(directory), captured.ToString()); }
        finally { Console.SetError(error); }
    }
}