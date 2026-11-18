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
    }
}