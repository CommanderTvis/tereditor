using System.Collections;
using System.Diagnostics;
using System.Globalization;

namespace Tereditor.Checks;

/// <summary>Slice <c>Index</c> of <c>Total</c>, one-based, over the gate's cases in the order they are reached.</summary>
internal readonly record struct Shard(int Index, int Total)
{
    internal static readonly Shard Whole = new(1, 1);

    internal static Shard Parse(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var total) || index < 1 || index > total)
            throw new ArgumentException($"Shard must be k/N with 1 <= k <= N, not '{text}'.");
        return new(index, total);
    }

    /// <summary>Lane <c>this</c> of a machine that owns <paramref name="job"/>: every job's lanes together cover each case once.</summary>
    internal Shard Within(Shard job) => new((job.Index - 1) * Total + Index, job.Total * Total);

    internal bool Owns(int ordinal) => ordinal % Total == Index - 1;

    public override string ToString() => $"{Index}/{Total}";
}

/// <summary>A case the previous run did not finish: it failed at <c>Ordinal</c>, leaving the rest of its shard unrun.</summary>
internal readonly record struct Unfinished(int Ordinal, Shard Shard, string Name, bool Exact = false)
{
    internal const string Setup = "(setup)";

    internal static Unfinished Parse(string line)
    {
        var parts = line.Split(' ', 3);
        return new(int.Parse(parts[0].TrimStart('!'), CultureInfo.InvariantCulture), Shard.Parse(parts[1]), parts[2], parts[0].StartsWith('!'));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{(Exact ? "!" : "")}{Ordinal} {Shard} {Name}");
}

/// <summary>Which cases one process runs: its shard, narrowed by named cases or by what the last run left unfinished.</summary>
internal sealed record Selection(Shard Shard, IReadOnlyList<Unfinished>? Repair = null, IReadOnlySet<string>? Only = null, CaseLane? Lane = null)
{
    internal bool Selects(string name, int ordinal, bool slow = false)
        => Shard.Owns(ordinal) && Lane switch { CaseLane.Merge => !slow, CaseLane.Slow => slow, _ => true } && Only?.Contains(name) != false && Repair?.Any(entry => (entry.Exact ? ordinal == entry.Ordinal : ordinal >= entry.Ordinal) && entry.Shard.Owns(ordinal)) != false;

    /// <summary>
    /// What a later repair run must cover after this one stopped at <paramref name="failed"/>: that case and
    /// whatever this process still owed behind it. A failure in shared setup leaves everything it owned.
    /// </summary>
    internal IReadOnlyList<Unfinished> Remaining((int Ordinal, string Name)? failed)
    {
        if (failed is not { } at) return Repair ?? [new(0, Shard, Unfinished.Setup)];
        return Repair is null
            ? [new(at.Ordinal, Shard, at.Name)]
            : [.. Repair.Select(entry => entry.Ordinal >= at.Ordinal ? entry : entry with { Ordinal = at.Ordinal, Name = at.Name })];
    }
}

/// <summary>Which cases a run takes: the fast ones that gate a merge, or the slow ones that run on a schedule or by dispatch. No lane means every case.</summary>
internal enum CaseLane { Merge, Slow }

/// <summary>
/// Names the gate's cases and decides which of them this process runs. Code outside a case is shared
/// setup and runs in every lane; a case runs in exactly one.
/// </summary>
internal static class Gate
{
    private static readonly HashSet<string> seen = [];
    private static readonly List<string> passed = [];
    private static Selection selection = new(Shard.Whole);
    private static bool listing;
    private static int ordinal;
    private static (int Ordinal, string Name)? running;
    private static int skipped;
    private static bool keepGoing;
    private static int? orderSeed;
    private static readonly List<(int Ordinal, string Name, Action Body)> deferred = [];
    private static readonly List<CaseResult> results = [];
    private static readonly List<CaseFailure> failures = [];
    private static readonly List<CaseTiming> timings = [];

    internal static IReadOnlyList<string> Passed => passed;
    internal static int Skipped => skipped;
    internal static string? Running => running?.Name;
    internal static IReadOnlyList<CaseFailure> Failures => failures;
    internal static IReadOnlyList<CaseTiming> Timings => timings;
    internal static bool KeepGoing => keepGoing;
    internal static Shard Shard => selection.Shard;

    /// <summary>What a repair run owes after an abort: the failing case and the rest of its shard, plus every case keep-going saw fail.</summary>
    internal static IReadOnlyList<Unfinished> Remaining => [.. selection.Remaining(running), .. Failed];

    /// <summary>The cases keep-going saw fail, each owed alone: the cases behind them ran.</summary>
    internal static IReadOnlyList<Unfinished> Failed => [.. failures.Select(failure => new Unfinished(failure.Ordinal, selection.Shard, failure.Name, Exact: true))];

    internal static IReadOnlyList<CaseResult> Results => results;

    /// <summary>The seed that shuffles the order cases run in, or null for the order they are written in.</summary>
    internal static int? OrderSeed => orderSeed;

    internal static void Configure(Selection cases, bool list, bool continueAfterFailure = false, int? seed = null)
    {
        selection = cases; listing = list; keepGoing = continueAfterFailure; orderSeed = seed;
    }

    /// <summary>Registers a case. A <paramref name="slow"/> case still takes its ordinal, so no shard changes; only the Slow lane (nightly.yml, slow.yml) and a run without a lane take it.</summary>
    internal static void Case(string name, Action body, bool slow = false)
    {
        if (!seen.Add(name)) throw new InvalidOperationException("Duplicate gate case: " + name);
        var index = ordinal++;
        if (selection.Repair?.FirstOrDefault(entry => entry.Ordinal == index) is { Name: { } expected } && expected != Unfinished.Setup && expected != name)
            throw new InvalidOperationException($"The case list changed since the failed run ({expected} is now {name}); run the whole gate.");
        if (!selection.Selects(name, index, slow)) { skipped++; return; }
        if (listing) { Console.WriteLine("CASE " + name); return; }
        // With a seed the cases are only collected here (shared setup between them has run by then) and RunDeferred runs them shuffled.
        if (orderSeed is not null) { deferred.Add((index, name, body)); return; }
        Execute(index, name, body);
    }

    /// <summary>Runs the cases a seeded run collected, in the seed's order.</summary>
    internal static void RunDeferred()
    {
        if (orderSeed is not { } seed) return;
        var order = deferred.ToArray();
        deferred.Clear();
        new Random(seed).Shuffle(order);
        Console.WriteLine($"ORDER seed {seed}: {string.Join(' ', order.Select(entry => entry.Name))}");
        foreach (var (index, name, body) in order) Execute(index, name, body);
    }

    private static void Execute(int index, string name, Action body)
    {
        running = (index, name);
        CiReport.BeginCase(name);
        // Names every case in the log before it runs, so output below belongs to it.
        Console.WriteLine("START " + name);
        var moment = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        if (!keepGoing)
        {
            try { body(); }
            catch (Exception error)
            {
                // The runner reports this one; the result file still needs it.
                var failure = CiReport.Describe(name, index, error, Stopwatch.GetElapsedTime(started));
                results.Add(new(name, index, false, moment, failure.Duration, failure.Message, error.ToString(), failure.Tail));
                throw;
            }
            CiReport.EndCase();
            var duration = Stopwatch.GetElapsedTime(started);
            timings.Add(new(name, duration, false));
            results.Add(new(name, index, true, moment, duration));
            Console.WriteLine($"END {name}: passed in {duration.TotalSeconds:F1}s");
            running = null;
            passed.Add(name);
            return;
        }
        // Cases share the process, so a failed one must not leak its directory or environment into the next.
        var directory = Directory.GetCurrentDirectory();
        var environment = Environment.GetEnvironmentVariables();
        try
        {
            body();
            CiReport.EndCase();
            var duration = Stopwatch.GetElapsedTime(started);
            timings.Add(new(name, duration, false));
            results.Add(new(name, index, true, moment, duration));
            Console.WriteLine($"END {name}: passed in {duration.TotalSeconds:F1}s");
            passed.Add(name);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            Console.Error.WriteLine(error);
            var failure = CiReport.Describe(name, index, error, elapsed);
            failures.Add(failure);
            results.Add(new(name, index, false, moment, elapsed, failure.Message, error.ToString(), failure.Tail));
            timings.Add(new(name, elapsed, true));
            CiReport.EndCase();
            Console.WriteLine($"FAILED {name}; continuing with the next case (keep-going)");
        }
        finally
        {
            Restore(directory, environment);
            running = null;
        }
    }

    private static void Restore(string directory, IDictionary environment)
    {
        try { if (Directory.GetCurrentDirectory() != directory) Directory.SetCurrentDirectory(directory); }
        catch (IOException) { }
        var now = Environment.GetEnvironmentVariables();
        foreach (var key in now.Keys.Cast<string>().Where(key => !environment.Contains(key)).ToArray()) Environment.SetEnvironmentVariable(key, null);
        foreach (var key in environment.Keys.Cast<string>())
            if (!Equals(now[key], environment[key])) Environment.SetEnvironmentVariable(key, (string?)environment[key]);
    }
}