using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Tereditor.Checks;

/// <summary>One gate case as it ran: what the result file records and the flake comparer reads back.</summary>
internal sealed record CaseResult(string Name, int Ordinal, bool Passed, DateTimeOffset Started, TimeSpan Duration, string? Message = null, string? Trace = null, string Output = "");

/// <summary>
/// Writes the gate's cases as a Visual Studio test result file (TRX), the format <c>dotnet test</c> produces and CI
/// tools read: one result per case with its outcome, duration, error message, stack trace and output. Hand-rolled
/// because the checks are a custom runner, not a test framework. <c>TEREDITOR_CHECKS_RESULTS</c> names the directory.
/// </summary>
internal static class TestResults
{
    internal const string Variable = "TEREDITOR_CHECKS_RESULTS";

    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private const string UnitTestType = "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b", ListId = "8c84fa94-04c1-424b-9868-57a2d4851a1d";

    internal static string? Directory => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? Path.GetFullPath(value) : null;

    /// <summary>Writes <c>checks-&lt;label&gt;.trx</c> into the results directory when one is configured; the label names the shard so jobs and lanes never collide.</summary>
    internal static void Write(string label, IReadOnlyList<CaseResult> results, DateTimeOffset started)
    {
        if (Directory is not { } directory) return;
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"checks-{label}.trx");
        var temporary = path + ".tmp";
        Create("Tereditor.Checks " + label, results, started, DateTimeOffset.UtcNow).Save(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    internal static XDocument Create(string name, IReadOnlyList<CaseResult> results, DateTimeOffset started, DateTimeOffset finished)
    {
        static string Time(TimeSpan span) => span.ToString("c", CultureInfo.InvariantCulture);
        static string At(DateTimeOffset moment) => moment.ToString("O", CultureInfo.InvariantCulture);
        var computer = Environment.MachineName;
        var ids = results.Select(result => (Result: result, Test: Id(result.Name), Execution: Id("execution " + result.Name + result.Started.Ticks))).ToArray();
        var failed = results.Count(result => !result.Passed);
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "TestRun",
                new XAttribute("id", Id("run " + name + started.Ticks)), new XAttribute("name", name),
                new XElement(Ns + "Times", new XAttribute("creation", At(started)), new XAttribute("queuing", At(started)), new XAttribute("start", At(started)), new XAttribute("finish", At(finished))),
                new XElement(Ns + "Results", ids.Select(entry => new XElement(Ns + "UnitTestResult",
                    new XAttribute("executionId", entry.Execution), new XAttribute("testId", entry.Test), new XAttribute("testName", entry.Result.Name),
                    new XAttribute("computerName", computer), new XAttribute("duration", Time(entry.Result.Duration)),
                    new XAttribute("startTime", At(entry.Result.Started)), new XAttribute("endTime", At(entry.Result.Started + entry.Result.Duration)),
                    new XAttribute("testType", UnitTestType), new XAttribute("outcome", entry.Result.Passed ? "Passed" : "Failed"), new XAttribute("testListId", ListId),
                    new XElement(Ns + "Output",
                        new XElement(Ns + "StdOut", Clean(entry.Result.Output)),
                        entry.Result.Passed ? null : new XElement(Ns + "ErrorInfo",
                            new XElement(Ns + "Message", Clean(entry.Result.Message ?? "")),
                            new XElement(Ns + "StackTrace", Clean(entry.Result.Trace ?? ""))))))),
                new XElement(Ns + "TestDefinitions", ids.Select(entry => new XElement(Ns + "UnitTest",
                    new XAttribute("name", entry.Result.Name), new XAttribute("id", entry.Test),
                    new XElement(Ns + "Execution", new XAttribute("id", entry.Execution)),
                    new XElement(Ns + "TestMethod", new XAttribute("codeBase", "Tereditor.Checks"), new XAttribute("className", "Tereditor.Checks.Gate"), new XAttribute("name", entry.Result.Name))))),
                new XElement(Ns + "TestEntries", ids.Select(entry => new XElement(Ns + "TestEntry",
                    new XAttribute("testId", entry.Test), new XAttribute("executionId", entry.Execution), new XAttribute("testListId", ListId)))),
                new XElement(Ns + "TestLists", new XElement(Ns + "TestList", new XAttribute("name", "Results Not in a List"), new XAttribute("id", ListId))),
                new XElement(Ns + "ResultSummary", new XAttribute("outcome", failed == 0 ? "Completed" : "Failed"),
                    new XElement(Ns + "Counters",
                        new XAttribute("total", results.Count), new XAttribute("executed", results.Count), new XAttribute("passed", results.Count - failed),
                        new XAttribute("failed", failed), new XAttribute("error", 0), new XAttribute("timeout", 0), new XAttribute("aborted", 0),
                        new XAttribute("inconclusive", 0), new XAttribute("passedButRunAborted", 0), new XAttribute("notRunnable", 0),
                        new XAttribute("notExecuted", 0), new XAttribute("disconnected", 0), new XAttribute("warning", 0),
                        new XAttribute("completed", 0), new XAttribute("inProgress", 0), new XAttribute("pending", 0)))));
    }

    /// <summary>A name's own id, stable across runs so a tool can follow one case from run to run.</summary>
    private static string Id(string text) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToString();

    // XML 1.0 cannot carry most control characters; terminal output has plenty. Surrogate halves stay (pairs are valid).
    private static string Clean(string text) => new([.. text.Where(character => char.IsSurrogate(character) || XmlConvert.IsXmlChar(character))]);

    /// <summary>The outcome per case name of a result file.</summary>
    internal static IReadOnlyList<(string Name, string Outcome, string? Message)> Read(string path)
        => [.. XDocument.Load(path).Descendants(Ns + "UnitTestResult").Select(result => (
            (string)result.Attribute("testName")!, (string)result.Attribute("outcome")!, result.Descendants(Ns + "Message").FirstOrDefault()?.Value))];
}