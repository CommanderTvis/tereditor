using System.Text.RegularExpressions;

namespace Tereditor.Plugins.ClaudeCode.Host.ClaudeConfig;

internal sealed record ScopedPath(ClaudeConfigScope Scope, string Path);

/// <summary>A <c>.claude/rules</c> folder; <c>Base</c> is the folder its rules' <c>paths</c> patterns start from.</summary>
internal sealed record RulesDirectory(ClaudeConfigScope Scope, string Path, string Base);

/// <summary>Every file and directory Claude Code reads its configuration from, in precedence order where it matters.</summary>
internal static partial class ClaudePaths
{
    public static string ClaudeHome() => ClaudeEnvironment.ClaudeHome();

    private static string ManagedSettingsPath() =>
        OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode/managed-settings.json"
        : OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode\managed-settings.json"
        : "/etc/claude-code/managed-settings.json";

    private static string ManagedInstructionsPath() =>
        OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode/CLAUDE.md"
        : OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode\CLAUDE.md"
        : "/etc/claude-code/CLAUDE.md";

    public static IReadOnlyList<ScopedPath> SettingsPaths(string root) =>
    [
        new(ClaudeConfigScope.Managed, ManagedSettingsPath()),
        new(ClaudeConfigScope.Local, Path.Combine(root, ".claude", "settings.local.json")),
        new(ClaudeConfigScope.Project, Path.Combine(root, ".claude", "settings.json")),
        new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "settings.json"))
    ];

    // Claude Code reads AGENTS.md in a directory that has no CLAUDE.md.
    private static string DirectoryInstructionsPath(string directory)
    {
        var claude = Path.Combine(directory, "CLAUDE.md");
        var agents = Path.Combine(directory, "AGENTS.md");
        return !File.Exists(claude) && File.Exists(agents) ? agents : claude;
    }

    private static IEnumerable<string> DirectoriesFromRoot(string cwd)
    {
        var directories = new List<string>();
        for (var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)); ; directory = Path.GetDirectoryName(directory)!)
        {
            directories.Insert(0, directory);
            if (Path.GetDirectoryName(directory) is null) return directories;
        }
    }

    /// <summary>The user's instructions, then every directory's from the filesystem root down to <paramref name="cwd"/>, then its <c>.claude/CLAUDE.md</c>.</summary>
    public static IReadOnlyList<ScopedPath> InstructionPaths(string cwd) =>
    [
        new(ClaudeConfigScope.Managed, ManagedInstructionsPath()),
        new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "CLAUDE.md")),
        .. DirectoriesFromRoot(cwd).SelectMany(directory => new ScopedPath[]
        {
            new(ClaudeConfigScope.Project, DirectoryInstructionsPath(directory)),
            new(ClaudeConfigScope.Local, Path.Combine(directory, "CLAUDE.local.md"))
        }),
        new(ClaudeConfigScope.Project, Path.Combine(cwd, ".claude", "CLAUDE.md"))
    ];

    /// <summary>The user's rules, then the project rules of every folder from just under the filesystem root down to <paramref name="cwd"/>.</summary>
    public static IReadOnlyList<RulesDirectory> RulesDirectories(string cwd) =>
    [
        new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "rules"), cwd),
        .. DirectoriesFromRoot(cwd).Skip(1).Select(directory => new RulesDirectory(ClaudeConfigScope.Project, Path.Combine(directory, ".claude", "rules"), directory))
    ];

    // Claude Code never follows a link to a network share, since looking such a path up can contact the host it names.
    // On Windows \\?\C:\ and \\.\ spell local paths, and \\wsl$ does not count.
    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith("/net/", StringComparison.Ordinal) || path.StartsWith("/Network/", StringComparison.Ordinal)) return true;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
        return !path.StartsWith(@"\\.\", StringComparison.Ordinal) && !path.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="path"/> with every link along it followed, parents included. Null for a link loop, a link that cannot
    /// be read, and a link that leads to a network share, which is never touched.
    /// </summary>
    public static string? RealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var current = Path.GetPathRoot(full)!;
            var pending = new Stack<string>();
            void Push(string tail)
            {
                foreach (var part in tail.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Reverse()) pending.Push(part);
            }
            Push(full[current.Length..]);
            var followed = 0;
            while (pending.TryPop(out var part))
            {
                if (part == ".") continue;
                if (part == "..") { current = Path.GetDirectoryName(current) ?? current; continue; }
                var next = Path.Combine(current, part);
                if (followed > 0 && IsNetworkPath(next)) return null;
                if (new FileInfo(next).LinkTarget is not { } target) { current = next; continue; }
                if (++followed > 40 || IsNetworkPath(target)) return null;
                if (Path.IsPathRooted(target))
                {
                    current = Path.GetPathRoot(target)!;
                    Push(target[current.Length..]);
                }
                else Push(target);
            }
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public static IReadOnlyList<ScopedPath> McpPaths(string root) => [new(ClaudeConfigScope.Project, Path.Combine(root, ".mcp.json"))];

    public static string ClaudeStatePath() => ClaudeEnvironment.ClaudeStatePath();

    public static IReadOnlyList<ScopedPath> SkillDirectories(string root) =>
        [new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "skills")), new(ClaudeConfigScope.Project, Path.Combine(root, ".claude", "skills"))];

    public static IReadOnlyList<ScopedPath> AgentDirectories(string root) =>
        [new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "agents")), new(ClaudeConfigScope.Project, Path.Combine(root, ".claude", "agents"))];

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NotAlphanumeric();

    public static string MemoryIndexPath(string root) =>
        Path.Combine(ClaudeHome(), "projects", NotAlphanumeric().Replace(root, "-"), "memory", "MEMORY.md");
}