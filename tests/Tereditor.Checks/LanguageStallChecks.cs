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
        // Lines as IntelliJ server 263.7084.0 wrote them while importing real Gradle 9.7.0 projects that fail in the usual ways.
        static string Progress(string step) => $"2026-11-23 12:03:22,163 [   2502]   INFO - #c.i.l.s.i.ImportExecutionKt - [IMPORT PROGRESS]: {step}";
        static string Err(string text) => $"2026-11-23 12:03:22,189 [   2528]   WARN - #c.i.l.s.i.ImportExecutionKt - [IMPORT ERR]: {text}";
        static string Log(params string[] lines) => string.Join("\n", lines);
        var healthy = Log("2026-11-23 11:55:58,116 [   1794]   INFO - #c.i.l.s.i.ImportExecutionKt - Trying to import using gradle", Progress("Run build started"), Progress("Resolve dependencies of :compileClasspath succeeded"), Progress("Run build succeeded"));
        Require(IntelliJImport.Failure(healthy) is null, "A healthy import was read as a failed one.");
        var jdk = Log(healthy, Progress("Run build started"), Progress("Resolve dependencies of :compileClasspath failed"), Progress("Run build failed"), Err(""), Err("FAILURE: Build failed with an exception."), Err(""), Err("* What went wrong:"),
            Err("Could not resolve all dependencies for configuration ':compileClasspath'."), Err("> Could not resolve all dependencies for configuration ':compileClasspath'."),
            Err("   > Failed to calculate the value of task ':compileJava' property 'javaCompiler'."),
            Err("      > Cannot find a Java installation on your machine (Mac OS X 26.7.3 aarch64) matching: {languageVersion=99, vendor=any vendor, implementation=vendor-specific, nativeImageCapable=false}. Toolchain download repositories have not been configured."),
            Err(""), Err("* Try:"), Err("> Run with --info or --debug option to get more log output."), Err("org.gradle.api.GradleException: later line"));
        var found = IntelliJImport.Failure(jdk);
        Require(found is not null && found.StartsWith("Cannot find a Java installation", StringComparison.Ordinal) && found.Contains("projectJdk", StringComparison.Ordinal) && !found.Contains("later line", StringComparison.Ordinal), "The JDK toolchain failure was not found with its hint: " + found);
        Require(IntelliJImport.Failure(Log(jdk, Progress("Run build started"), Progress("Run build succeeded"))) is null, "A later successful import left the failure in place.");
        Require(IntelliJImport.Failure(Log(Progress("Run build failed"), Err(""), Err("FAILURE: Build failed with an exception."))) is null, "A message block that is still being written was reported.");
        var verification = IntelliJImport.Failure(Log(Progress("Run build failed"), Err(""), Err("FAILURE: Build failed with an exception."), Err("* What went wrong:"), Err("Dependency verification failed for configuration ':compileClasspath'"),
            Err("3 artifacts failed verification:"), Err("  - gson-2.10.1.jar (com.google.code.gson:gson:2.10.1) from repository MavenRepo"), Err("* Try:")));
        Require(verification is not null && verification.StartsWith("Dependency verification failed for configuration", StringComparison.Ordinal) && verification.Contains("verification-metadata.xml", StringComparison.Ordinal), "The dependency verification failure has no hint: " + verification);
        var script = IntelliJImport.Failure(Log(Progress("Run build failed"), Err("e: file:///work/syntax/build.gradle.kts:3:32: Expecting an argument"), Err("e: file:///work/syntax/build.gradle.kts:3:33: Expecting '}'"), Err(""),
            Err("FAILURE: Build failed with an exception."), Err("* Where:"), Err("Build file '/work/syntax/build.gradle.kts' line: 3"), Err("* What went wrong:"), Err("Script compilation errors:"), Err("* Try:")));
        Require(script == "build.gradle.kts:3:32: Expecting an argument", "A build script error was not read: " + script);
        var plugin = IntelliJImport.Failure(Log(Progress("Run build failed"), Err("FAILURE: Build failed with an exception."), Err("* Where:"), Err("Build file '/work/plugin/build.gradle.kts' line: 1"), Err("* What went wrong:"),
            Err("Plugin [id: 'com.example.does-not-exist', version: '1.0'] was not found in any of the following sources:"), Err("- Gradle Core Plugins (plugin is not in 'org.gradle' namespace)"), Err("* Try:")));
        Require(plugin == "Plugin [id: 'com.example.does-not-exist', version: '1.0'] was not found in any of the following sources:", "A missing plugin was not read: " + plugin);

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