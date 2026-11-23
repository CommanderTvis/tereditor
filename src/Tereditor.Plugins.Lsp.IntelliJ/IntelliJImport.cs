using System.Text.RegularExpressions;

namespace Tereditor.Plugins.Lsp.IntelliJ;

/// <summary>Finds a failed Gradle or Maven import in the server's own log, which the server never reports through the protocol.</summary>
internal static partial class IntelliJImport
{
    private const string Progress = "[IMPORT PROGRESS]: ";
    private const string Error = "[IMPORT ERR]: ";
    private const string Jdk = "Cannot find a Java installation";
    private const string Toolchain = "Toolchain download repositories have not been configured";
    private const string Verification = "Dependency verification failed";

    /// <summary>Where the server writes its log under the session directory it was given as <c>--system-path</c>.</summary>
    internal static IReadOnlyList<string> LogFiles(string sessionDirectory) => sessionDirectory.Length == 0 ? [] :
        [Path.Combine(sessionDirectory, "log", "intellij-server.log"), Path.Combine(sessionDirectory, "system", "log", "intellij-server.log")];

    /// <summary>
    /// What the last import run in the log says: the server logs <c>[IMPORT PROGRESS]: Run build failed</c> or <c>succeeded</c> and then the
    /// build tool's own messages as <c>[IMPORT ERR]:</c> lines. Returns the most telling message with the usual fix, cut to 300 characters;
    /// <see langword="null"/> when the last run did not fail.
    /// </summary>
    internal static string? Failure(string log)
    {
        var failed = false;
        var complete = false;
        var messages = new List<string>();
        foreach (var raw in log.Split('\n'))
        {
            if (raw.IndexOf(Progress, StringComparison.Ordinal) is var p and >= 0)
            {
                var step = raw[(p + Progress.Length)..].Trim();
                if (step == "Run build failed") { failed = true; complete = false; messages.Clear(); }
                else if (step == "Run build succeeded") { failed = false; complete = false; messages.Clear(); }
            }
            else if (failed && raw.IndexOf(Error, StringComparison.Ordinal) is var e and >= 0)
            {
                var message = raw[(e + Error.Length)..].TrimEnd();
                if (message.TrimStart() is "* Try:" or "* Exception is:") { failed = false; complete = true; }
                else if (message.Trim().Length > 0) messages.Add(message.Trim());
            }
        }
        // The progress line comes a moment before the tool's messages: a block that is still being written is read again on the next call.
        if (failed) return null;
        if (!complete) return Fallback(log);
        var cause = messages.FirstOrDefault(line => line.Contains(Jdk, StringComparison.Ordinal) || line.Contains(Toolchain, StringComparison.Ordinal) || line.Contains(Verification, StringComparison.Ordinal))
            ?? messages.FirstOrDefault(line => line.StartsWith("e: ", StringComparison.Ordinal))
            ?? (messages.IndexOf("* What went wrong:") is >= 0 and var wrong && wrong + 1 < messages.Count ? messages[wrong + 1] : null)
            ?? messages.FirstOrDefault(line => !line.StartsWith("FAILURE:", StringComparison.Ordinal) && !line.StartsWith("* ", StringComparison.Ordinal))
            ?? "The build tool reported an error.";
        cause = ScriptError().Replace(cause.TrimStart('>', ' '), "$1");
        return (cause.Length > 300 ? cause[..300] + "…" : cause) + Hint(cause);
    }

    // A Kotlin script error names the whole file URI; the file name and position are what a reader needs.
    [GeneratedRegex(@"^e: file://\S*/([^/\s]+:\d+:\d+: .*)$")]
    private static partial Regex ScriptError();

    private static string Hint(string line) =>
        line.Contains(Jdk, StringComparison.Ordinal) || line.Contains(Toolchain, StringComparison.Ordinal)
            ? " The build needs a JDK for its toolchain. Set the project JDK in the workspace settings (projectJdk) to one that matches, or configure toolchain download repositories in the build."
            : line.Contains(Verification, StringComparison.Ordinal)
                ? " Gradle dependency verification rejected a file that only the IDE import asks for (it also fetches -sources.jar and -src.zip files). Add its checksum to gradle/verification-metadata.xml."
                : "";

    // Maven's log lines were never seen; these two phrases come from a user report.
    private static string? Fallback(string log)
    {
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("import failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Failed to import", StringComparison.Ordinal))
                return line.Length > 300 ? line[..300] + "…" : line;
        }
        return null;
    }
}