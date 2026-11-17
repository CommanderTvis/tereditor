using System.Text.Json.Nodes;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>
/// What tereditor offers a session beyond the conversation: the address of its MCP server (workspace, title, visualize and
/// code-intelligence tools) and the instruction text every tereditor agent gets. What the agent receives follows what it
/// advertised in <c>initialize</c>, so no agent is special-cased: HTTP servers go to an agent with <c>mcpCapabilities.http</c>,
/// the text to one with <c>sessionCapabilities.systemPrompt</c> (append mode).
/// </summary>
internal sealed record AcpSessionSetup(string? McpUrl, string? Instructions)
{
    public const string ServerName = "tereditor";

    /// <summary>The value recorded on the agent's entry: what a chat gave it, or "" before its first chat.</summary>
    public static string Support(AcpAgentInfo agent) =>
        (agent.McpHttp, agent.SystemPromptAppend) switch { (true, true) => "both", (true, false) => "tools", (false, true) => "instructions", _ => "none" };

    /// <summary>The <c>mcpServers</c> of <c>session/new</c> and <c>session/load</c>: tereditor's server over HTTP, or none.</summary>
    public JsonArray McpServers(AcpAgentInfo agent) =>
        McpUrl is { Length: > 0 } url && agent.McpHttp
            ? [new JsonObject { ["type"] = "http", ["name"] = ServerName, ["url"] = url, ["headers"] = new JsonArray() }]
            : [];

    /// <summary>The <c>systemPrompt</c> in append mode, or null when there is no text or the agent takes none.</summary>
    public JsonObject? SystemPrompt(AcpAgentInfo agent) =>
        Instructions is { Length: > 0 } text && agent.SystemPromptAppend
            ? new JsonObject { ["mode"] = "append", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) }
            : null;
}

/// <summary>How a chat gets its <see cref="AcpSessionSetup"/> and reports what the agent can take.</summary>
/// <param name="Setup">Computed once per start, off the UI thread; it reads the project's instruction file.</param>
/// <param name="Seen">Told the agent's support (<see cref="AcpSessionSetup.Support"/>) after <c>initialize</c>.</param>
internal sealed record AcpSessionLink(Func<AcpSessionSetup> Setup, Action<string> Seen);

/// <summary>The text appended to an agent's own instructions: that it runs in tereditor, then the sections the host and enabled plugins contribute.</summary>
internal static class AcpSystemPrompt
{
    private const string Header = """
        # You are running inside tereditor

        This is a tereditor workspace chat, not a bare shell; the user sees your files, editor, diff and terminals.

        """;

    private const string ToolsIntro = "Prefer available tereditor MCP tools; disabled plugins may hide tools:\n\n";

    public static string Compose(IReadOnlyList<string> tools, IReadOnlyList<string> sections) =>
        Header + (tools.Count == 0 ? "" : "\n" + ToolsIntro + string.Concat(tools.Select(item => "- " + item + "\n")))
        + string.Concat(sections.Select(section => "\n" + section.TrimEnd('\n') + "\n"));
}