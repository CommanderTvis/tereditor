namespace Tereditor.Plugins.Acp.Host;

/// <summary>
/// Files an agent can change to make code run later or to widen what it may do: Git hooks, editor and agent settings, shell start-up
/// files and MCP configuration. A write to one asks the user first. The list is data: add a rule here, nowhere else.
/// </summary>
internal static class AcpSensitivePaths
{
    /// <summary>A path is sensitive when its segments contain these, in order, whatever the case.</summary>
    private sealed record Rule(string[] Segments, string Why);

    private static readonly Rule[] Rules =
    [
        new([".git", "hooks"], "Git hooks run commands on Git actions."),
        new([".git", "config"], "Git settings can run commands."),
        new([".husky"], "Husky hooks run commands on Git actions."),
        new([".claude"], "These are Claude Code settings, hooks and commands."),
        new([".codex"], "These are Codex settings."),
        new([".vscode"], "These are editor settings and tasks."),
        new([".cursor"], "These are editor and agent settings."),
        new([".idea"], "These are editor settings."),
        new([".tereditor"], "These are tereditor project settings."),
        new([".mcp.json"], "This configures MCP servers, which run programs."),
        new(["mcp.json"], "This configures MCP servers, which run programs."),
        new([".envrc"], "direnv runs this file when you enter the folder."),
        .. new[] { ".bashrc", ".bash_profile", ".bash_login", ".profile", ".zshrc", ".zprofile", ".zshenv", ".zlogin", ".config/fish/config.fish" }
            .Select(name => new Rule(name.Split('/'), "The shell runs this file when it starts."))
    ];

    /// <summary>Why a write to the path needs the user's yes, or null when it does not. The path is relative to the workspace root.</summary>
    public static string? Reason(string relativePath)
    {
        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rule in Rules)
            for (var start = 0; start + rule.Segments.Length <= parts.Length; start++)
                if (rule.Segments.Select((segment, index) => string.Equals(segment, parts[start + index], StringComparison.OrdinalIgnoreCase)).All(same => same))
                    return rule.Why;
        return null;
    }
}