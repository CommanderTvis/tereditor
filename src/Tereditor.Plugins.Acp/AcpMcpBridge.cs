using System.Text;
using System.Text.Json.Nodes;

namespace Tereditor.Plugins.Acp;

/// <summary>
/// The stdio MCP server an ACP agent without HTTP MCP support starts: a relay of tereditor's HTTP MCP route. Every line on stdin is one
/// JSON-RPC message, posted to the URL in <see cref="UrlVariable"/>; the answer is written as one line on stdout. A notification gets no
/// answer. It is a hidden mode of the tereditor executable (<see cref="Argument"/>); the URL, which holds the chat's token, travels in the
/// environment, never in the arguments.
/// </summary>
public static class AcpMcpBridge
{
    public const string Argument = "--acp-mcp-bridge";
    public const string UrlVariable = "TEREDITOR_ACP_MCP_URL";

    /// <summary>Runs as the process: stdin to stdout against the URL in the environment. Returns the exit code.</summary>
    public static int Run()
    {
        if (Environment.GetEnvironmentVariable(UrlVariable) is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine("tereditor MCP bridge: " + UrlVariable + " is not set.");
            return 2;
        }
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        RelayAsync(input, output, uri, client).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>
    /// Relays until <paramref name="input"/> ends, then waits for the requests still running. Requests run at the same time, since a tool can
    /// take a while and the agent may ask for another meanwhile; each answer is one line, written whole.
    /// </summary>
    public static async Task RelayAsync(Stream input, Stream output, Uri url, HttpClient client)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var gate = new Lock();
        var running = new List<Task>();
        async Task WriteAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (gate)
            {
                output.Write(bytes);
                output.Flush();
            }
            await Task.CompletedTask;
        }
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            var message = line;
            running.Add(Task.Run(async () =>
            {
                if (await AnswerAsync(message, url, client) is { } answer) await WriteAsync(answer);
            }));
            running.RemoveAll(task => task.IsCompleted);
        }
        await Task.WhenAll(running);
    }

    // The line to write for one message, or null for none: a notification, or a 202 with no body. A failed request still gets an
    // error answer for its id, so the agent never waits on a reply that will not come.
    private static async Task<string?> AnswerAsync(string message, Uri url, HttpClient client)
    {
        var id = IdOf(message);
        try
        {
            using var content = new StringContent(message, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content);
            var body = (await response.Content.ReadAsStringAsync()).Trim();
            if (body.Length > 0 && JsonNode.Parse(body) is { } parsed && (response.IsSuccessStatusCode || parsed is JsonObject { } reply && reply.ContainsKey("error"))) return parsed.ToJsonString();
            return id is null || response.IsSuccessStatusCode ? null : Error(id, "tereditor answered HTTP " + (int)response.StatusCode + ".");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return id is null ? null : Error(id, "tereditor could not be reached: " + error.Message);
        }
    }

    private static JsonNode? IdOf(string message)
    {
        try { return JsonNode.Parse(message) is JsonObject { } request && request["id"] is { } id ? id.DeepClone() : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string Error(JsonNode id, string text) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32000, ["message"] = text } }.ToJsonString();
}