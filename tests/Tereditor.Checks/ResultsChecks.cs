using System.Diagnostics;
using System.Xml.Linq;

namespace Tereditor.Checks;

/// <summary>
/// The result file and the flake comparer: a TRX that records a failing and a passing case, one written by the real
/// runner (case names before they start, a seeded order that repeats, expected failures kept apart), and
/// <c>scripts/flaky-compare.cs</c> on fixture files.
/// </summary>
internal static class ResultsChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "results");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);
        Format(directory);
        Gated(Path.Combine(directory, "runner"));
        Comparer(Path.Combine(directory, "compare"));
        Console.WriteLine("PASS results: TRX with a failing and a passing case, case names and seeded order from the runner, flake comparer");
    }

    private static void Format(string directory)
    {
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        CaseResult[] results =
        [
            new("alpha", 0, true, at, TimeSpan.FromSeconds(1.5)),
            new("beta", 1, false, at, TimeSpan.FromSeconds(2), "It broke <&>", "at X in /y.cs:line 3", "output with a control \x01 character"),
        ];
        var path = Path.Combine(directory, "format.trx");
        TestResults.Create("selftest", results, at, at.AddSeconds(4)).Save(path);
        var read = TestResults.Read(path);
        Require(read.Count == 2 && read[0] is ("alpha", "Passed", null) && read[1] is ("beta", "Failed", "It broke <&>"), "The TRX must record a passing and a failing case with the failure message.");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var document = XDocument.Load(path);
        var counters = document.Descendants(ns + "Counters").Single();
        Require((int)counters.Attribute("total")! == 2 && (int)counters.Attribute("passed")! == 1 && (int)counters.Attribute("failed")! == 1, "The TRX counters must match the results.");
        Require(document.Root!.Element(ns + "ResultSummary")!.Attribute("outcome")!.Value == "Failed", "A run with a failure must summarize as Failed.");
        Require(document.Descendants(ns + "StackTrace").Single().Value.Contains("line 3", StringComparison.Ordinal), "The failure's stack trace must be kept.");
        Require(document.Descendants(ns + "UnitTest").Count() == 2 && document.Descendants(ns + "TestEntry").Count() == 2, "Every case needs a definition and an entry.");
        var testIds = document.Descendants(ns + "UnitTestResult").Select(result => (string)result.Attribute("testId")!).ToArray();
        Require(testIds.SequenceEqual(TestResults.Create("selftest", results, at, at).Descendants(ns + "UnitTestResult").Select(result => (string)result.Attribute("testId")!)), "A case keeps its id from run to run.");
    }

    private static (int Code, string Output, string[] Started) Gate(string directory, string results, params string[] args)
    {
        var start = RunnerChecks.Self(args);
        start.WorkingDirectory = directory;
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.Environment[RunnerChecks.FixtureCases] = "8";
        start.Environment["TEREDITOR_CHECKS_FIXTURE_FAIL"] = "c1";
        start.Environment[Runner.KeepGoingVariable] = "1";
        start.Environment[TestResults.Variable] = results;
        start.Environment.Remove(Runner.JobShardVariable);
        start.Environment.Remove(Runner.OwnerVariable);
        start.Environment.Remove(Runner.ResultVariable);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var all = output + error.GetAwaiter().GetResult();
        return (process.ExitCode, all, [.. output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("START ", StringComparison.Ordinal)).Select(line => line[6..])]);
    }

    private static void Gated(string directory)
    {
        Directory.CreateDirectory(directory);
        var results = Path.Combine(directory, "trx");
        var plain = Gate(directory, results);
        Require(plain.Code == 1 && plain.Started.SequenceEqual(Enumerable.Range(0, 8).Select(index => "c" + index)), "Every case must announce itself in written order.\n" + plain.Output);
        var file = Path.Combine(results, "checks-1of1.trx");
        Require(File.Exists(file), "The runner must write checks-1of1.trx.\n" + plain.Output);
        var read = TestResults.Read(file);
        Require(read.Count == 8 && read.Count(result => result.Outcome == "Passed") == 7 && read.Single(result => result.Outcome == "Failed") is ("c1", _, { } message) && message.Contains("Fixture case threw: c1", StringComparison.Ordinal),
            "The result file must record seven passes and the failure of c1 with its message.");

        var seeded = Gate(directory, results, "--order-seed", "3");
        var again = Gate(directory, results, "--order-seed", "3");
        var other = Gate(directory, results, "--order-seed", "4");
        Require(seeded.Output.Contains("ORDER SEED 3", StringComparison.Ordinal), "A seeded run must print its seed.");
        Require(seeded.Started.Length == 8 && seeded.Started.Order().SequenceEqual(plain.Started) && seeded.Started.SequenceEqual(again.Started), "A seed must give the same order of the same cases.");
        Require(!seeded.Started.SequenceEqual(other.Started) && !seeded.Started.SequenceEqual(plain.Started), "Another seed, or none, must give another order.");
        Require(TestResults.Read(file).Count == 8, "A seeded run must write its results too.");
    }

    private static void Comparer(string directory)
    {
        if (BoundaryChecks.Checkout() is not { } checkout) { Console.WriteLine("SKIP flake comparer: no source checkout"); return; }
        var at = DateTimeOffset.UtcNow;
        var runs = Path.Combine(directory, "flaky");
        void Write(string name, bool betaPasses)
        {
            var folder = Path.Combine(runs, name);
            Directory.CreateDirectory(folder);
            TestResults.Create("fixture", [new("alpha", 0, true, at, TimeSpan.Zero), new("beta", 1, betaPasses, at, TimeSpan.Zero, betaPasses ? null : "boom")], at, at)
                .Save(Path.Combine(folder, "checks-1of1.trx"));
        }
        Write("run-1-seed-1", true); Write("run-2-seed-2", false); Write("run-3-seed-3", true);
        var flaky = Script(checkout, runs);
        if (flaky is null) { Console.WriteLine("SKIP flake comparer: dotnet cannot run the script here"); return; }
        Require(flaky.Value.Code == 1 && flaky.Value.Output.Contains("| `beta` | Passed | Failed | Passed |", StringComparison.Ordinal) && flaky.Value.Output.Contains("order seed 2", StringComparison.Ordinal) && !flaky.Value.Output.Contains("| `alpha`", StringComparison.Ordinal),
            "The comparer must name the case that changed outcome and only that one.\n" + flaky.Value.Output);
        Write("run-2-seed-2", true);
        var stable = Script(checkout, runs)!.Value;
        Require(stable.Code == 0 && stable.Output.Contains("same outcome in every run", StringComparison.Ordinal), "Identical runs must pass.\n" + stable.Output);
    }

    private static (int Code, string Output)? Script(string checkout, string directory)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(directory)! };
        foreach (var argument in new[] { "run", "--file", Path.Combine(checkout, "scripts", "flaky-compare.cs"), "--", directory }) start.ArgumentList.Add(argument);
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment.Remove("GITHUB_ACTIONS");
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        using (process)
        {
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromMinutes(3))) { process.Kill(true); throw new InvalidOperationException("The comparer script timed out."); }
            return (process.ExitCode, output + error.GetAwaiter().GetResult());
        }
    }
}