namespace Tereditor.Plugins.Lsp.IntelliJ;

/// <summary>Finds a failed Gradle or Maven import in the server's own log, which the server never reports through the protocol.</summary>
internal static class IntelliJImport
{
    private const string Jdk = "Cannot find a Java installation";
    private const string Toolchain = "Toolchain download repositories have not been configured";
    private const string Verification = "Dependency verification failed";

    /// <summary>Where the server writes its log under the session directory it was given as <c>--system-path</c>.</summary>
    internal static IReadOnlyList<string> LogFiles(string sessionDirectory) => sessionDirectory.Length == 0 ? [] :
        [Path.Combine(sessionDirectory, "log", "intellij-server.log"), Path.Combine(sessionDirectory, "system", "log", "intellij-server.log")];

    /// <summary>The first log line that says the import failed, cut to 300 characters, with the usual fix; <see langword="null"/> when there is none.</summary>
    internal static string? Failure(string log)
    {
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var hint = line.Contains(Jdk, StringComparison.Ordinal) || line.Contains(Toolchain, StringComparison.Ordinal)
                ? " The build needs a JDK for its toolchain. Set the project JDK in the workspace settings (projectJdk) to one that matches, or configure toolchain download repositories in the build."
                : line.Contains(Verification, StringComparison.Ordinal)
                    ? " Gradle dependency verification rejected a file that only the IDE import asks for (it also fetches -sources.jar and -src.zip files). Add its checksum to gradle/verification-metadata.xml."
                    : line.Contains("import failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Failed to import", StringComparison.Ordinal) ? "" : null;
            if (hint is not null) return (line.Length > 300 ? line[..300] + "…" : line) + hint;
        }
        return null;
    }
}