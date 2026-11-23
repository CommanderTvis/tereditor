using System.Text.Json.Nodes;

using Tereditor.Host.Core;

namespace Tereditor.Checks;

/// <summary>MCP resources over one request each: the capability is advertised, the list follows the tool table, a read returns the tool's data, and an unknown URI or a failed read is an MCP error.</summary>
internal static class McpResourceChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static JsonNode? Request(string method, JsonObject? parameters = null) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject()
    };

    public static async Task Run()
    {
        var schema = new JsonObject { ["type"] = "object" };
        McpServer.McpTool status = new("lsp_status", "lsp status", "Fake status.", schema,
            (arguments, _) => Task.FromResult(($"servers for {arguments["operation"]}", false)));
        McpServer.McpTool processes = new("processes_status", "processes status", "Fake guard.", schema,
            (_, _) => Task.FromResult(("guard on", false)));
        McpServer.McpTool failing = new("lsp_status", "lsp status", "Fake failing status.", schema,
            (_, _) => Task.FromResult(("no server", true)));
        IReadOnlyList<McpServer.McpTool> table = [status, processes];

        var resources = McpResources.Create(table);
        Require(resources.Select(item => item.Uri).SequenceEqual(["tereditor://language/servers", "tereditor://processes/memory-guard"]),
            "Resources must follow the tools on the table.");
        Require(McpResources.Create([processes]).Count == 1, "A resource must go with its tool: no lsp_status, no language resource.");

        var (_, initialized) = await McpServer.HandleAsync(Request("initialize"), table, default, resources: resources);
        Require(initialized!["result"]!["capabilities"]!["resources"] is not null, "The resources capability is not advertised.");

        var (_, listed) = await McpServer.HandleAsync(Request("resources/list"), table, default, resources: resources);
        var items = listed!["result"]!["resources"]!.AsArray();
        Require(items.Count == 2 && items[0]!["mimeType"]!.ToString() == "application/json" && items[0]!["description"] is not null,
            "resources/list lost an entry, its MIME type or its description.");

        var (_, read) = await McpServer.HandleAsync(Request("resources/read", new JsonObject { ["uri"] = "tereditor://language/servers" }), table, default, resources: resources);
        var content = read!["result"]!["contents"]!.AsArray()[0]!;
        Require(content["text"]!.ToString() == "servers for servers" && content["uri"]!.ToString() == "tereditor://language/servers" && content["mimeType"]!.ToString() == "application/json",
            "A read did not return the tool's data with its URI and MIME type: " + content.ToJsonString());

        var (_, unknown) = await McpServer.HandleAsync(Request("resources/read", new JsonObject { ["uri"] = "tereditor://nope" }), table, default, resources: resources);
        Require(unknown!["error"]!["code"]!.GetValue<int>() == -32002, "An unknown URI is not the resource-not-found error.");

        var (_, missingUri) = await McpServer.HandleAsync(Request("resources/read"), table, default, resources: resources);
        Require(missingUri!["error"]!["code"]!.GetValue<int>() == -32602, "A read without a URI is not an invalid-params error.");

        var (_, templates) = await McpServer.HandleAsync(Request("resources/templates/list"), table, default, resources: resources);
        Require(templates!["result"]!["resourceTemplates"]!.AsArray().Count == 0, "Templates must be an empty list.");

        var (_, none) = await McpServer.HandleAsync(Request("resources/list"), table);
        Require(none!["result"]!["resources"]!.AsArray().Count == 0, "A host with no resources must list none.");

        var failed = McpResources.Create([failing]);
        var (_, failedRead) = await McpServer.HandleAsync(Request("resources/read", new JsonObject { ["uri"] = "tereditor://language/servers" }), [failing], default, resources: failed);
        Require(failedRead!["error"]!["code"]!.GetValue<int>() == -32603, "A failed tool behind a resource must be an internal error.");

        Console.WriteLine("PASS MCP resources: capability, list follows tools, read, unknown URI, missing URI, templates, failed read");
    }
}