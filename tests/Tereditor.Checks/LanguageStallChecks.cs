using Tereditor.Host.Core.Language;

namespace Tereditor.Checks;

/// <summary>A language server whose progress line stops changing is reported as stuck, with the usual Gradle causes.</summary>
internal static class LanguageStallChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static void Run()
    {
        var after = LanguageSession.StallAfter;
        Require(LanguageSession.StallText("2%; Gradle", after - TimeSpan.FromSeconds(1)).Length == 0, "A fresh progress line was reported as stuck.");
        var gradle = LanguageSession.StallText("2%; Gradle", after);
        Require(gradle.Contains("no progress for 5 min at \"2%; Gradle\"", StringComparison.Ordinal) && gradle.Contains("Gradle wrapper distribution", StringComparison.Ordinal), "A stalled Gradle line has no diagnosable note: " + gradle);
        var other = LanguageSession.StallText("Indexing…", TimeSpan.FromMinutes(9));
        Require(other.Contains("no progress for 9 min", StringComparison.Ordinal) && !other.Contains("Gradle", StringComparison.Ordinal), "A stalled non-Gradle line carries Gradle advice: " + other);
        var log = new LanguageLog();
        for (var index = 0; index < 50; index++) log.Add(index % 2 == 0 ? "stderr" : "window/logMessage", "line " + index);
        log.Add("project", "not the server");
        var tail = log.ServerTail(LanguageSession.StallLogLines).Split('\n');
        Require(tail.Length == 20 && tail[^1].Contains("line 49", StringComparison.Ordinal) && tail.All(line => !line.Contains("not the server", StringComparison.Ordinal)), "The server tail is not the last 20 server lines.");
        var directory = Path.Combine(Path.GetTempPath(), "stall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "log"));
        try
        {
            var block = LanguageSession.StallLogs(string.Join("\n", tail), directory);
            Require(block.Contains(directory, StringComparison.Ordinal) && block.Contains(Path.Combine(directory, "log"), StringComparison.Ordinal) && block.Contains("line 49", StringComparison.Ordinal), "The stall block lacks the log paths or lines: " + block);
        }
        finally { Directory.Delete(directory, true); }
    }
}