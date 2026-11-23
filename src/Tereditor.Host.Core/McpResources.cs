using System.Text.Json.Nodes;

namespace Tereditor.Host.Core;

/// <summary>
/// The read-only resources an agent reads by URI. Each one reads the data of a tool already on the table, so it is listed
/// only while that tool is: a plugin turned off, or a tool a filter removed, takes its resource away with it.
/// </summary>
public static class McpResources
{
    public static IReadOnlyList<McpServer.McpResource> Create(IReadOnlyList<McpServer.McpTool> tools)
    {
        List<McpServer.McpResource> resources = [];
        Add("lsp_status", "tereditor://language/servers", "Language servers",
            "Each language server of this workspace with its state, as lsp_status operation=servers lists them. Reading starts no server.",
            new JsonObject { ["operation"] = "servers" });
        Add("processes_status", "tereditor://processes/memory-guard", "Memory guard",
            "Whether the terminal memory guard is on, its limits, each terminal's memory against its limit and the guard's recent events, as processes_status reports them.",
            new JsonObject());
        return resources;

        void Add(string toolName, string uri, string name, string description, JsonObject arguments)
        {
            if (tools.FirstOrDefault(tool => tool.Name == toolName) is not { } tool) return;
            resources.Add(new(uri, name, description, "application/json", async token =>
            {
                var (text, error) = await tool.Call((JsonObject)arguments.DeepClone(), token);
                if (error) throw new InvalidOperationException(text);
                return text;
            }));
        }
    }
}