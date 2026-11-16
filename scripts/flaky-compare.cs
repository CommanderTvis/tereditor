#!/usr/bin/env dotnet
// Compares the TRX result files of several runs of the checks and names the flake candidates: cases whose outcome
// differs between runs, which includes a case that fails only under some case order. A case that fails in every run
// is reported apart as a plain failure. The report goes to the console and, under GitHub Actions, the step summary.
// Usage: dotnet run --file scripts/flaky-compare.cs -- <directory>
// The directory holds one subdirectory per run (named run-<n>, optionally ending -seed-<seed>), each with *.trx files.
// Exit code: 0 stable, 1 flake candidates or failures, 2 usage or no results.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length != 1 || !Directory.Exists(args[0])) { Console.Error.WriteLine("usage: flaky-compare.cs -- <directory of run-<n> directories>"); return 2; }

XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
var runs = Directory.GetDirectories(args[0]).Order(StringComparer.Ordinal).Select(path =>
{
    var name = Path.GetFileName(path);
    var seed = Regex.Match(name, @"-seed-(\d+)$") is { Success: true } match ? match.Groups[1].Value : null;
    var outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.GetFiles(path, "*.trx", SearchOption.AllDirectories))
        foreach (var result in XDocument.Load(file).Descendants(ns + "UnitTestResult"))
            outcomes[(string)result.Attribute("testName")!] = (string)result.Attribute("outcome")!;
    return (Name: name, Seed: seed, Outcomes: outcomes);
}).Where(run => run.Outcomes.Count > 0).ToList();

if (runs.Count < 2) { Console.Error.WriteLine($"Need result files from at least two runs; found {runs.Count} run(s) with results in {args[0]}."); return 2; }

var cases = runs.SelectMany(run => run.Outcomes.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
string Outcome(Dictionary<string, string> outcomes, string name) => outcomes.TryGetValue(name, out var value) ? value : "Missing";
var flaky = cases.Where(name => runs.Select(run => Outcome(run.Outcomes, name)).Distinct().Count() > 1).ToList();
var failing = cases.Where(name => runs.All(run => Outcome(run.Outcomes, name) == "Failed")).ToList();

var report = new StringBuilder();
report.AppendLine(CultureInfo.InvariantCulture, $"### Flaky check candidates: {flaky.Count} of {cases.Count} cases, {runs.Count} runs").AppendLine();
report.AppendLine("Runs: " + string.Join(", ", runs.Select(run => run.Seed is null ? run.Name : $"{run.Name} (order seed {run.Seed})")) + ".").AppendLine();
if (flaky.Count > 0)
{
    report.AppendLine("A case below passed in some runs and did not in others. Reproduce with `--order-seed <seed>` of a failing run.").AppendLine();
    report.AppendLine("| Case | " + string.Join(" | ", runs.Select(run => run.Name)) + " |");
    report.AppendLine("| --- |" + string.Concat(runs.Select(_ => " --- |")));
    foreach (var name in flaky) report.AppendLine($"| `{name}` | " + string.Join(" | ", runs.Select(run => Outcome(run.Outcomes, name))) + " |");
    report.AppendLine();
}
if (failing.Count > 0)
{
    report.AppendLine($"Failed in every run (a real failure, not a flake): {string.Join(", ", failing.Select(name => $"`{name}`"))}.").AppendLine();
}
if (flaky.Count == 0 && failing.Count == 0) report.AppendLine("Every case had the same outcome in every run.").AppendLine();

Console.Write(report);
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) File.AppendAllText(summary, report.ToString());
if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
    foreach (var name in flaky) Console.WriteLine($"::error title=Flake candidate::{name} changed outcome between runs");
return flaky.Count > 0 || failing.Count > 0 ? 1 : 0;
