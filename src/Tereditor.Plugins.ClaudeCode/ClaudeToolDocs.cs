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