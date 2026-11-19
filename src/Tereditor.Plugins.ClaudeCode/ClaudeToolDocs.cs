namespace Tereditor.Plugins.ClaudeCode;

// Where the tools reference documents each built-in tool (https://code.claude.com/docs/en/tools-reference). The value is the
// heading id of the tool's own section, a shared section for a family, or empty for a tool the page lists only in its table.
public static class ClaudeToolDocs
{
    public const string DocsUrl = "https://code.claude.com/docs/en/tools-reference";

    public static readonly IReadOnlyDictionary<string, string> Anchors = new Dictionary<string, string>
    {
        ["Agent"] = "agent-tool-behavior",
        ["AskUserQuestion"] = "askuserquestion-tool-behavior",
        ["Bash"] = "bash-tool-behavior",
        ["CronCreate"] = "",
        ["CronDelete"] = "",
        ["CronList"] = "",
        ["Edit"] = "edit-tool-behavior",
        ["EndConversation"] = "endconversation-tool-behavior",
        ["EnterPlanMode"] = "",
        ["EnterWorktree"] = "enterworktree-tool-behavior",
        ["ExitWorktree"] = "enterworktree-tool-behavior",
        ["Glob"] = "glob-tool-behavior",
        ["Grep"] = "grep-tool-behavior",
        ["ListMcpResourcesTool"] = "",
        ["LSP"] = "lsp-tool-behavior",
        ["Monitor"] = "monitor-tool",
        ["NotebookEdit"] = "notebookedit-tool-behavior",
        ["PowerShell"] = "powershell-tool",
        ["Read"] = "read-tool-behavior",
        ["ReadMcpResourceTool"] = "",
        ["SendFeedback"] = "sendfeedback-tool-behavior",
        ["Skill"] = "",
        ["TaskCreate"] = "task-tool-availability",
        ["TaskGet"] = "task-tool-availability",
        ["TaskList"] = "task-tool-availability",
        ["TaskStop"] = "task-tool-availability",
        ["TaskUpdate"] = "task-tool-availability",
        ["TodoWrite"] = "task-tool-availability",
        ["WebFetch"] = "webfetch-tool-behavior",
        ["WebSearch"] = "websearch-tool-behavior",
        ["Workflow"] = "",
        ["Write"] = "write-tool-behavior"
    };

    /// <summary>The docs URL of a known built-in tool, or null for any other (an MCP tool, an unknown name).</summary>
    public static string? Url(string tool) =>
        Anchors.TryGetValue(tool, out var anchor) ? anchor.Length == 0 ? DocsUrl : DocsUrl + "#" + anchor : null;
}

// Where the docs describe Claude Code's bundled skills (the ones with no file on disk). A skill with a section of its own on the skills
// page maps to that heading id; the others map to the commands reference's list, which marks each of them as a skill.
public static class ClaudeSkillDocs
{
    public const string SkillsUrl = "https://code.claude.com/docs/en/skills";
    public const string ListUrl = "https://code.claude.com/docs/en/commands#all-commands";

    public static readonly IReadOnlyDictionary<string, string> Urls = new Dictionary<string, string>
    {
        ["artifact-capabilities"] = ListUrl,
        ["artifact-diagramming"] = ListUrl,
        ["batch"] = ListUrl,
        ["claude-api"] = SkillsUrl + "#work-on-claude-api-projects",
        ["claude-in-chrome"] = ListUrl,
        ["code-review"] = ListUrl,
        ["dataviz"] = ListUrl,
        ["debug"] = ListUrl,
        ["design"] = ListUrl,
        ["design-sync"] = ListUrl,
        ["doctor"] = SkillsUrl + "#check-your-setup-with-/doctor",
        ["fewer-permission-prompts"] = ListUrl,
        ["loop"] = ListUrl,
        ["run"] = SkillsUrl + "#run-and-verify-your-app",
        ["run-skill-generator"] = SkillsUrl + "#run-and-verify-your-app",
        ["simplify"] = ListUrl,
        ["slides"] = ListUrl,
        ["update-config"] = ListUrl,
        ["verify"] = SkillsUrl + "#run-and-verify-your-app",
        ["workflow-authoring"] = ListUrl
    };

    /// <summary>The docs URL of a known bundled skill, or null for any other (a user, project or plugin skill, an unknown name).</summary>
    public static string? Url(string skill) => Urls.GetValueOrDefault(skill);
}