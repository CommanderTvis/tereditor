using System.Text.Json.Nodes;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>The agent's answer to <c>initialize</c>, reduced to what the client uses.</summary>
internal sealed record AcpAgentInfo(int ProtocolVersion, bool LoadSession, bool Image, bool Audio, bool EmbeddedContext, IReadOnlyList<AcpAuthMethod> AuthMethods, string? Name, string? Version, bool CanListSessions, JsonObject Raw)
{
    /// <summary>Whether the agent says it answers auth/status (a <c>status</c> entry under <c>agentCapabilities.auth</c>).</summary>
    public bool AdvertisesAuthStatus => ((Raw["agentCapabilities"] as JsonObject)?["auth"] as JsonObject)?.ContainsKey("status") == true;

    /// <summary>Whether the agent connects to MCP servers over HTTP (<c>mcpCapabilities.http</c>); tereditor's server is HTTP only.</summary>
    public bool McpHttp => (((Raw["agentCapabilities"] as JsonObject)?["mcpCapabilities"] as JsonObject)?["http"] as JsonValue)?.TryGetValue<bool>(out var on) == true && on;

    /// <summary>Whether the agent takes a client system prompt (<c>sessionCapabilities.systemPrompt</c>, an unstable field; a bare object means append mode).</summary>
    public bool SystemPromptAppend => ((Raw["agentCapabilities"] as JsonObject)?["sessionCapabilities"] as JsonObject)?["systemPrompt"] is JsonObject;
}

/// <summary>
/// A sign-in the agent offers. A terminal login is a method of <c>type</c> "terminal" (the client runs the agent program with <see cref="Args"/> and <see cref="Env"/>)
/// or one with <c>_meta["terminal-auth"]</c> (its own <see cref="Command"/>); GitHub Copilot CLI sends the second form with no <c>type</c>.
/// </summary>
internal sealed record AcpAuthMethod(string Id, string Name, string? Description)
{
    public bool Terminal { get; init; }
    /// <summary>The program of a terminal login; null means the agent's own program.</summary>
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public string? Label { get; init; }

    public static AcpAuthMethod Parse(JsonObject method)
    {
        var meta = (method["_meta"] as JsonObject)?["terminal-auth"] as JsonObject;
        var source = meta ?? method;
        var args = (source["args"] as JsonArray ?? []).Select(arg => arg is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToArray();
        var env = new Dictionary<string, string>();
        if (source["env"] is JsonObject map)
            foreach (var (name, value) in map)
                if (value is JsonValue text && text.TryGetValue<string>(out var content)) env[name] = content;
        return new AcpAuthMethod(AcpFolder.Str(method, "id"), AcpFolder.Str(method, "name"), AcpFolder.Str(method, "description") is { Length: > 0 } description ? description : null)
        {
            Terminal = meta is not null || AcpFolder.Str(method, "type") == "terminal",
            Command = meta is not null && AcpFolder.Str(meta, "command") is { Length: > 0 } command ? command : null,
            Args = args,
            Env = env,
            Label = meta is not null && AcpFolder.Str(meta, "label") is { Length: > 0 } label ? label : null
        };
    }
}

/// <summary>Raised when the agent speaks a protocol version this client does not.</summary>
public sealed class AcpVersionException(int agent) : InvalidOperationException($"The agent speaks ACP version {agent}; tereditor supports version {AcpClient.ProtocolVersion}.")
{
    public int AgentVersion { get; } = agent;
}

/// <summary>What the client side serves to the agent: the methods the agent calls and the updates it streams.</summary>
internal interface IAcpClientHandler
{
    /// <summary>A <c>session/update</c> notification's <c>update</c> object.</summary>
    void Update(string sessionId, JsonObject update);
    /// <summary>An agent-to-client request other than session/update (fs/*, terminal/*, session/request_permission).</summary>
    Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken);
}

/// <summary>The client end of the Agent Client Protocol (stable version 1) over one <see cref="AcpConnection"/>.</summary>
internal sealed class AcpClient : IAsyncDisposable
{
    public const int ProtocolVersion = 1;

    private readonly AcpConnection connection;

    public AcpClient(Stream fromAgent, Stream toAgent, IAcpClientHandler handler)
    {
        connection = new AcpConnection(fromAgent, toAgent,
            (method, parameters, ct) => handler.RequestAsync(method, parameters as JsonObject ?? new JsonObject(), ct),
            (method, parameters) =>
            {
                if (method == "session/update" && parameters is JsonObject p && p["sessionId"]?.GetValue<string>() is { } id && p["update"] is JsonObject update) handler.Update(id, update);
            });
    }

    public Task Closed => connection.Closed;

    /// <summary>
    /// Advertises the client capabilities (file and terminal methods) and negotiates the version: an agent that answers with a
    /// higher version than asked is not understood, so the client refuses it rather than guessing.
    /// </summary>
    public async Task<AcpAgentInfo> InitializeAsync(string clientName, string clientVersion, CancellationToken cancellationToken)
    {
        var result = await connection.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = true, ["writeTextFile"] = true },
                ["terminal"] = true,
                // The client can run the agent program itself in a terminal, so the agent may offer a terminal sign-in.
                ["auth"] = new JsonObject { ["terminal"] = true },
                ["session"] = new JsonObject { ["configOptions"] = new JsonObject { ["boolean"] = new JsonObject() } }
            },
            ["clientInfo"] = new JsonObject { ["name"] = clientName.ToLowerInvariant(), ["title"] = clientName, ["version"] = clientVersion }
        }, cancellationToken) as JsonObject ?? throw new AcpException(AcpException.Internal, "The agent answered initialize with no result.");
        var version = result["protocolVersion"]?.GetValue<int>() ?? 0;
        if (version != ProtocolVersion) throw new AcpVersionException(version);
        var capabilities = result["agentCapabilities"] as JsonObject;
        var prompt = capabilities?["promptCapabilities"] as JsonObject;
        var info = result["agentInfo"] as JsonObject;
        return new AcpAgentInfo(version,
            capabilities?["loadSession"]?.GetValue<bool>() == true,
            prompt?["image"]?.GetValue<bool>() == true, prompt?["audio"]?.GetValue<bool>() == true, prompt?["embeddedContext"]?.GetValue<bool>() == true,
            [.. (result["authMethods"] as JsonArray ?? []).OfType<JsonObject>().Select(AcpAuthMethod.Parse)],
            info?["title"]?.GetValue<string>() ?? info?["name"]?.GetValue<string>(), info?["version"]?.GetValue<string>(),
            (capabilities?["sessionCapabilities"] as JsonObject)?.ContainsKey("list") == true, result);
    }

    /// <summary>
    /// auth/status, an unstable method: the accounts the agent uses. Ask only an agent that advertises it (<see cref="AcpAgentInfo.AdvertisesAuthStatus"/>);
    /// the schema is not stable, so the caller reads the reply leniently and ignores a failure.
    /// </summary>
    public Task<JsonNode?> AuthStatusAsync(CancellationToken cancellationToken) =>
        connection.RequestAsync("auth/status", new JsonObject(), cancellationToken);

    public Task<JsonNode?> AuthenticateAsync(string methodId, CancellationToken cancellationToken) =>
        connection.RequestAsync("authenticate", new JsonObject { ["methodId"] = methodId }, cancellationToken);

    /// <summary>Starts a session; the result carries <c>sessionId</c> and, when offered, <c>modes</c>, <c>models</c> and <c>configOptions</c>.</summary>
    public async Task<JsonObject> NewSessionAsync(string cwd, CancellationToken cancellationToken, JsonArray? mcpServers = null, JsonObject? systemPrompt = null) =>
        await connection.RequestAsync("session/new", SessionParams(new JsonObject { ["cwd"] = cwd }, mcpServers, systemPrompt), cancellationToken) as JsonObject
        ?? throw new AcpException(AcpException.Internal, "The agent answered session/new with no result.");

    /// <summary>Resumes a stored session; the agent replays the conversation as updates before this returns.</summary>
    public async Task<JsonObject> LoadSessionAsync(string sessionId, string cwd, CancellationToken cancellationToken, JsonArray? mcpServers = null, JsonObject? systemPrompt = null) =>
        await connection.RequestAsync("session/load", SessionParams(new JsonObject { ["sessionId"] = sessionId, ["cwd"] = cwd }, mcpServers, systemPrompt), cancellationToken) as JsonObject ?? new JsonObject();

    // The MCP servers are always present (the field is required); the system prompt only when the agent takes one.
    private static JsonObject SessionParams(JsonObject parameters, JsonArray? mcpServers, JsonObject? systemPrompt)
    {
        parameters["mcpServers"] = mcpServers ?? new JsonArray();
        if (systemPrompt is not null) parameters["systemPrompt"] = systemPrompt;
        return parameters;
    }

    /// <summary>Runs one turn. Returns the stop reason (<c>end_turn</c>, <c>max_tokens</c>, <c>max_turn_requests</c>, <c>refusal</c>, <c>cancelled</c>).</summary>
    public async Task<string> PromptAsync(string sessionId, JsonArray content, CancellationToken cancellationToken) =>
        (await connection.RequestAsync("session/prompt", new JsonObject { ["sessionId"] = sessionId, ["prompt"] = content }, cancellationToken) as JsonObject)?["stopReason"]?.GetValue<string>() ?? "end_turn";

    /// <summary>session/set_model, an unstable method some agents serve for the models they list; an agent without it answers method-not-found.</summary>
    public Task<JsonNode?> SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken) =>
        connection.RequestAsync("session/set_model", new JsonObject { ["sessionId"] = sessionId, ["modelId"] = modelId }, cancellationToken);

    public Task CancelAsync(string sessionId) => connection.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId });

    public Task<JsonNode?> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken) =>
        connection.RequestAsync("session/set_mode", new JsonObject { ["sessionId"] = sessionId, ["modeId"] = modeId }, cancellationToken);

    /// <summary>session/set_config_option; the answer carries the full set of options as they now stand. A boolean option is set with <c>type: "boolean"</c> and a JSON boolean.</summary>
    public Task<JsonNode?> SetConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken, bool boolean = false) =>
        connection.RequestAsync("session/set_config_option", boolean
            ? new JsonObject { ["sessionId"] = sessionId, ["configId"] = configId, ["type"] = "boolean", ["value"] = value == "true" }
            : new JsonObject { ["sessionId"] = sessionId, ["configId"] = configId, ["value"] = value }, cancellationToken);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}