using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Lsp.IntelliJ;

/// <summary>Finds Maven before the server imports a <c>pom.xml</c>, and removes the plugin-install poms its import leaves in the workspace.</summary>
internal static class IntelliJMaven
{
    internal const string NotFound = "Maven was not found. Put mvn on PATH, set MAVEN_HOME, or add mvnw to the project. Maven import is skipped; Java and Kotlin answers for single files still work.";

    private const string GeneratedPattern = "mavenPluginInstall-pom*.xml";
    private static readonly ConcurrentDictionary<string, Snapshot> Snapshots = new(StringComparer.Ordinal);

    private sealed class Snapshot(string[] directories, HashSet<string> before)
    {
        internal readonly string[] Directories = directories;
        internal readonly HashSet<string> Before = before;
        internal long Swept;
    }

    /// <summary>The Maven the import can run: the user's <c>mvn</c> on PATH, <c>MAVEN_HOME</c> or <c>M2_HOME</c>, else the project's wrapper; <see langword="null"/> when none exists.</summary>
    internal static (string Path, string Source)? Locate(string workspace, string? path, string? mavenHome, string? m2Home)
    {
        var names = OperatingSystem.IsWindows() ? new[] { "mvn.cmd", "mvn.bat", "mvn.exe" } : new[] { "mvn" };
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var name in names)
                if (File.Exists(Path.Combine(directory, name))) return (Path.Combine(directory, name), "PATH");
        foreach (var (variable, home) in new[] { ("MAVEN_HOME", mavenHome), ("M2_HOME", m2Home) })
            if (!string.IsNullOrWhiteSpace(home))
                foreach (var name in names)
                    if (File.Exists(Path.Combine(home, "bin", name))) return (Path.Combine(home, "bin", name), variable);
        var wrapper = Path.Combine(workspace, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
        return File.Exists(wrapper) ? (wrapper, "project wrapper") : null;
    }

    /// <summary>Whether this start asks for a Maven import: the importer is Maven, an initialization project is of type maven, or a lone <c>pom.xml</c> is the build.</summary>
    internal static bool Wanted(JsonObject result, string importer, string workspace) =>
        importer == "maven" || result["projects"] is JsonArray projects && projects.OfType<JsonObject>().Any(project => project["type"]?.ToString() == "maven")
        || importer.Length == 0 && File.Exists(Path.Combine(workspace, "pom.xml")) && !new[] { "settings.gradle.kts", "settings.gradle", "build.gradle.kts", "build.gradle" }.Any(name => File.Exists(Path.Combine(workspace, name)));

    /// <summary>Removes the Maven projects and a Maven importer choice from the initialization, so the server never waits on an import that cannot run.</summary>
    internal static void Skip(JsonObject result)
    {
        if (result["projects"] is JsonArray projects)
            for (var index = projects.Count - 1; index >= 0; index--)
                if (projects[index] is JsonObject project && project["type"]?.ToString() == "maven") projects.RemoveAt(index);
        if (result["buildTools"] is JsonObject tools)
            foreach (var key in tools.Where(pair => pair.Value?.ToString() == "maven").Select(pair => pair.Key).ToArray()) tools.Remove(key);
    }

    /// <summary>Remembers which plugin-install poms the user's workspace already has, so only later ones count as generated.</summary>
    internal static void Remember(string workspace, IEnumerable<string> manifests)
    {
        var directories = new[] { workspace }.Concat(manifests.Select(manifest => Path.GetDirectoryName(Path.Combine(workspace, manifest)) ?? workspace))
            .Select(Path.GetFullPath).Where(directory => directory == workspace || directory.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Distinct().ToArray();
        Snapshots[workspace] = new Snapshot(directories, Existing(directories));
    }

    private static HashSet<string> Existing(string[] directories)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
            try { foreach (var file in Directory.EnumerateFiles(directory, GeneratedPattern, SearchOption.TopDirectoryOnly)) found.Add(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>Deletes plugin-install poms that appeared after <see cref="Remember"/>; a file that was there before, or is a link, stays. At most once every 10 seconds.</summary>
    internal static int Sweep(string workspace, bool force = false)
    {
        if (!Snapshots.TryGetValue(workspace, out var snapshot)) return 0;
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref snapshot.Swept) < 10_000) return 0;
        Interlocked.Exchange(ref snapshot.Swept, now);
        var removed = 0;
        foreach (var file in Existing(snapshot.Directories))
        {
            if (snapshot.Before.Contains(file)) continue;
            try
            {
                if (new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                File.Delete(file);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    internal static void OnMessage(ILanguageServerSession session, string method, JsonNode? parameters)
    {
        IntelliJLicensing.OnMessage(session, method, parameters);
        Sweep(session.Workspace);
    }
}