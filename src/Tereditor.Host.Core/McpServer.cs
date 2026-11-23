using System.Text.Json.Nodes;

namespace Tereditor.Host.Core;

/// <summary>A minimal, stateless Model Context Protocol server over single JSON-RPC requests, serving agents in a
/// terminal exactly the tools its caller hands it. A tool failure is an <c>isError</c> result, never a protocol error.</summary>
public static class McpServer
{
    private const string LatestProtocol = "2025-06-18";
    private static readonly HashSet<string> KnownProtocols = ["2024-11-05", "2025-03-26", LatestProtocol];
    /// <summary>Tells agents where a running Java / Kotlin (IntelliJ) server's analysis is reached.</summary>
    public const string IntelliJNote = "Java and Kotlin analysis from the Java / Kotlin (IntelliJ) server comes through the lsp_* tools. A tereditor session has no mcp__IntelliJ__* tools: they belong to JetBrains' IDE plugin, which tereditor does not run.";

    private const string Instructions =
        "tereditor's workspace tools and enabled plugins' project tools for the workspace this session runs in. Use those relevant to the task and the project's workflow. set_title titles this terminal once the task is clear; workspace_delete removes a finished workspace you created. " + IntelliJNote;

    /// <summary>A host or active plugin tool on the table; it validates its own arguments.</summary>
    public sealed record McpTool(string Name, string Title, string Description, JsonObject InputSchema, Func<JsonObject, CancellationToken, Task<(string Text, bool Error)>> Call);

    /// <summary>A read-only resource, read by URI. Reading is idempotent and takes no action; a failed read throws.</summary>
    public sealed record McpResource(string Uri, string Name, string Description, string MimeType, Func<CancellationToken, Task<string>> Read);

    /// <summary>Answers one request. <paramref name="instructions"/> replaces the initialize text for a host whose tool table differs from a terminal's.</summary>
    public static async Task<(int Status, JsonNode? Body)> HandleAsync(JsonNode? message, IReadOnlyList<McpTool> tools, CancellationToken cancellationToken = default, string? instructions = null, IReadOnlyList<McpResource>? resources = null)
    {
        if (message is not JsonObject frame) return Error(null, -32600, "Expected a single JSON-RPC request object.");
        var id = frame["id"]?.DeepClone();
        if (Text(frame["jsonrpc"]) != "2.0" || Text(frame["method"]) is not { } method) return Error(id, -32600, "Not a JSON-RPC 2.0 request.");
        if (!frame.ContainsKey("id")) return (202, null);
        var parameters = frame["params"] as JsonObject ?? [];
        switch (method)
        {
            case "initialize":
                var requested = Text(parameters["protocolVersion"]);
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = requested is not null && KnownProtocols.Contains(requested) ? requested : LatestProtocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false }, ["resources"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "tereditor", ["version"] = "1" },
                    ["instructions"] = instructions ?? Instructions
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["title"] = tool.Title,
                        ["description"] = tool.Description,
                        ["inputSchema"] = tool.InputSchema.DeepClone()
                    })])
                });
            case "tools/call":
                var name = Text(parameters["name"]);
                if (tools.FirstOrDefault(tool => tool.Name == name) is not { } called) return Error(id, -32602, $"Unknown tool: {name}");
                var arguments = parameters["arguments"] as JsonObject ?? [];
                (string Text, bool Error) reply;
                try { reply = await called.Call(arguments, cancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException) { reply = ("Tool failed: " + error.Message, true); }
                var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply.Text }) };
                if (reply.Error) result["isError"] = true;
                return Result(id, result);
            case "resources/list":
                return Result(id, new JsonObject
                {
                    ["resources"] = new JsonArray([.. (resources ?? []).Select(item => (JsonNode)new JsonObject
                    {
                        ["uri"] = item.Uri,
                        ["name"] = item.Name,
                        ["description"] = item.Description,
                        ["mimeType"] = item.MimeType
                    })])
                });
            case "resources/templates/list":
                return Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() });
            case "resources/read":
                var resourceUri = Text(parameters["uri"]);
                if (resourceUri is null) return Error(id, -32602, "resources/read needs a uri.");
                if ((resources ?? []).FirstOrDefault(item => item.Uri == resourceUri) is not { } read) return Error(id, -32002, $"Resource not found: {resourceUri}");
                string readText;
                try { readText = await read.Read(cancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException) { return Error(id, -32603, "Resource failed: " + error.Message); }
                return Result(id, new JsonObject
                {
                    ["contents"] = new JsonArray(new JsonObject { ["uri"] = resourceUri, ["mimeType"] = read.MimeType, ["text"] = readText })
                });
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;

    private static (int, JsonNode?) Result(JsonNode? id, JsonNode result) =>
        (200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

    private static (int, JsonNode?) Error(JsonNode? id, int code, string message) =>
        (200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
}