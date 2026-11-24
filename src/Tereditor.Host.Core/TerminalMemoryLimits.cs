namespace Tereditor.Host.Core;

/// <summary>
/// The memory guard's limits (the Processes plugin's settings, see Terminals SPEC "Memory guard"): <see cref="PerTerminalMb"/> caps
/// one terminal's process tree (0 derives it from physical memory), <see cref="TotalMb"/> caps all terminals together (0 derives it), a
/// process gets <see cref="GraceSeconds"/> to end after its stop request, and <see cref="Protect"/> names programs that are warned about, never ended.
/// </summary>
internal sealed record TerminalMemoryLimits(bool Enabled, int PerTerminalMb, int TotalMb, int GraceSeconds, IReadOnlyList<string> Protect)
{
    internal const int DefaultGraceSeconds = 10, MaxMb = 1_048_576, MinMb = 64;
    internal static readonly TerminalMemoryLimits Default = new(true, 0, 0, DefaultGraceSeconds, []);

    /// <summary>A terminal's limit in bytes: the setting, or a third of physical memory clamped to 4–32 GiB.</summary>
    internal long PerTerminalBytes(long physical) => PerTerminalMb > 0 ? PerTerminalMb * 1024L * 1024 : AutomaticPerTerminal(physical);

    /// <summary>All terminals' limit in bytes: the setting, or 60% of physical memory.</summary>
    internal long TotalBytes(long physical) => TotalMb > 0 ? TotalMb * 1024L * 1024 : AutomaticTotal(physical);

    internal static long AutomaticPerTerminal(long physical) => Math.Clamp(physical / 3, 4L << 30, 32L << 30);

    /// <summary>
    /// Build tools by executable name. The per-terminal limit and system pressure only warn about them (see Terminals SPEC "Memory guard") until
    /// one runs away; the user's <see cref="Protect"/> list adds to these and is never ended at all. A java below gradle counts too.
    /// </summary>
    internal static readonly IReadOnlyList<string> BuildTools =
        ["dotnet", "csc", "VBCSCompiler", "MSBuild", "cl", "link", "ld", "clang", "clang++", "rustc", "cargo", "go", "javac", "kotlinc", "gradle"];

    internal static bool IsBuildTool(string name)
    {
        var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return BuildTools.Any(tool => string.Equals(tool, bare, StringComparison.OrdinalIgnoreCase));
    }

    internal static long AutomaticTotal(long physical) => physical * 6 / 10;

    internal bool Protects(string name) => Protect.Any(entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The limits the Processes plugin's settings give. A value out of range falls back to its default (automatic limit, 10 s grace) and an
    /// unusable program name is dropped, so a hand-edited setting never stops the guard.
    /// </summary>
    internal static TerminalMemoryLimits From(int perTerminalMb, int totalMb, int graceSeconds, IReadOnlyList<string> protect)
    {
        static int Limit(int megabytes) => megabytes is >= MinMb and <= MaxMb ? megabytes : 0;
        var names = new List<string>();
        foreach (var entry in protect)
        {
            var name = entry?.Trim() ?? "";
            if (name.Length is 0 or > 64 || name.Contains('\0') || name.Contains('/') || name.Contains('\\')) continue;
            if (names.Count < 64 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return new(true, Limit(perTerminalMb), Limit(totalMb), graceSeconds is >= 1 and <= 600 ? graceSeconds : DefaultGraceSeconds, names);
    }
}