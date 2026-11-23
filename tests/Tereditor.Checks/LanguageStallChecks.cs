using Tereditor.Host.Abstractions;
using Tereditor.Host.Core.Language;
using Tereditor.Plugins.Lsp.IntelliJ;

namespace Tereditor.Checks;

/// <summary>A language server whose progress line stops changing is reported as stuck, with the usual Gradle causes.</summary>
internal static class LanguageStallChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static void Run()
    {
        Import();
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

    /// <summary>A failed Gradle import found in the server's own log is reported, never as a clean answer.</summary>
    private static void Import()
    {
        var healthy = "2026-11-23 10:00:01,100 [   500]   INFO - #c.i.s.Startup - Server started\n2026-11-23 10:00:02,200 [  1500]   INFO - #c.i.o.e.g.Import - Gradle project imported";
        Require(IntelliJImport.Failure(healthy) is null, "A healthy log was read as a failed import.");
        var jdk = healthy + "\n2026-11-23 10:00:09,300 [  9500]   WARN - #c.i.o.e.g.GradleExecution - Cannot find a Java installation on your machine matching this tasks requirements: {languageVersion=21}. Toolchain download repositories have not been configured.\n2026-11-23 10:00:10,000 [ 10000]   INFO - later line";
        var found = IntelliJImport.Failure(jdk);
        Require(found is not null && found.Contains("Cannot find a Java installation", StringComparison.Ordinal) && found.Contains("projectJdk", StringComparison.Ordinal) && !found.Contains("later line", StringComparison.Ordinal), "The JDK toolchain failure was not found with its hint: " + found);
        var verification = IntelliJImport.Failure("ERROR - Dependency verification failed for configuration ':app:compileClasspath'");
        Require(verification is not null && verification.Contains("verification-metadata.xml", StringComparison.Ordinal), "The dependency verification failure has no hint.");

        var directory = Path.Combine(Path.GetTempPath(), "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "log"));
        try
        {
            var files = IntelliJImport.LogFiles(directory);
            Require(files.Count == 2 && files[0].EndsWith("intellij-server.log", StringComparison.Ordinal), "The IntelliJ log files are not named.");
            Require(LanguageSession.ReadLogTailAsync(files, CancellationToken.None).GetAwaiter().GetResult().Length == 0, "A missing log read as text.");
            File.WriteAllText(files[0], new string('x', 200 * 1024) + "\n" + jdk);
            var tail = LanguageSession.ReadLogTailAsync(files, CancellationToken.None).GetAwaiter().GetResult();
            Require(tail.Length <= 128 * 1024 + 4 && IntelliJImport.Failure(tail) is not null, "The log tail lost the end of the log.");
        }
        finally { Directory.Delete(directory, true); }

        var note = LanguageSession.ImportNote("failed", found!);
        Require(note.Contains("empty or wrong", StringComparison.Ordinal) && note.Contains("Cannot find a Java installation", StringComparison.Ordinal), "The import note does not name the failure: " + note);
        var current = new LanguageResult("ready", "Nothing found.", "intellij") { Freshness = "current", Coverage = "x" };
        var marked = LanguageSession.WithImport(current, "failed", found!);
        Require(marked.Freshness == "partial" && marked.ImportState == "failed" && marked.Detail.StartsWith("Project import failed", StringComparison.Ordinal) && marked.Detail.EndsWith("Nothing found.", StringComparison.Ordinal), "A failed import left the answer current: " + marked.Freshness);
        Require(LanguageSession.WithImport(current, "", "") == current && LanguageSession.WithImport(current, "importing", "2%; Gradle import").Freshness == "current", "A healthy or running import changed the answer's freshness.");
        Require(LanguageSession.IsImportProgress("2%; Gradle") && LanguageSession.IsImportProgress("Importing project") && !LanguageSession.IsImportProgress("Indexing…"), "Import progress and index progress are not told apart.");
        var hit = new LanguageItem("a.py", 0, 0, 0, 0, "x", "symbol", "python");
        var silent = LanguageService.SilentServers([new("ready", "", "python") { Locations = [hit] }, new("ready", "", "intellij")]);
        Require(silent.StartsWith("intellij returned no symbols while python did", StringComparison.Ordinal) && LanguageService.SilentServers([new("ready", "", "intellij")]).Length == 0
            && LanguageService.SilentServers([new("ready", "", "python") { Locations = [hit] }]).Length == 0, "The silent server is not named: " + silent);
    }
}