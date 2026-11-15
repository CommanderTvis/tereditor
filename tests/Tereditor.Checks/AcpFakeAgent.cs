using System.IO.Pipelines;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Acp.Host;

namespace Tereditor.Checks;

// An in-process ACP agent for the checks: it speaks the real framing over a pipe pair and plays scripted turns chosen by the prompt text.
//   "hello"   thought, two message chunks, a tool call that completes with a diff, a plan; ends the turn.
//   "ask"     asks permission for a tool call and reports the chosen option as a message.
//   "read X"  calls fs/read_text_file for path X and reports the content (or the error).
//   "write X" calls fs/write_text_file for path X.
//   "run"     creates a terminal (echo hi), waits for exit and reports its output.
//   "ask2"    asks permission for a file edit with locations, raw input, a new-file diff and four options (once, always, no, never).
//   "wait"    streams a chunk and then waits for session/cancel; stops with "cancelled".
/// <summary>What <c>session/new</c> offers: the modes, only config options (with grouped models, a thought level and a switch), only models, or nothing.</summary>
internal enum AcpOffer { Modes, ConfigOptions, Models, Nothing }

internal sealed class AcpFakeAgent : IAsyncDisposable
{
    private readonly Pipe toAgent = new(), fromAgent = new();
    private readonly AcpConnection connection;
    private readonly Dictionary<string, TaskCompletionSource> cancels = [];
    public int ProtocolVersion { get; init; } = 1;
    public bool LoadSession { get; init; } = true;
    public List<string> Methods { get; } = [];
    public JsonObject? ClientCapabilities { get; private set; }
    private int sessions;

    /// <summary>The client's side of the pipes: read what the agent writes, write what the client sends.</summary>
    public Stream ClientInput => fromAgent.Reader.AsStream();
    public Stream ClientOutput => toAgent.Writer.AsStream();

    public AcpOffer Offer { get; }
    /// <summary>The params of the last request of each method.</summary>
    public Dictionary<string, JsonNode?> Last { get; } = [];
    private JsonArray configOptions = [];

    public AcpFakeAgent(int protocolVersion = 1, bool loadSession = true, AcpOffer offer = AcpOffer.Modes)
    {
        ProtocolVersion = protocolVersion;
        LoadSession = loadSession;
        Offer = offer;
        configOptions = Options();
        connection = new AcpConnection(toAgent.Reader.AsStream(), fromAgent.Writer.AsStream(), HandleAsync, Notified);
    }

    /// <summary>Ends the agent as a crashed process would: the client's input ends.</summary>
    public async Task KillAsync()
    {
        await fromAgent.Writer.CompleteAsync();
        await toAgent.Reader.CompleteAsync();
    }

    private void Notified(string method, JsonNode? parameters)
    {
        lock (Methods) Methods.Add(method);
        if (method == "session/cancel" && parameters?["sessionId"]?.GetValue<string>() is { } id)
            lock (cancels) { if (cancels.TryGetValue(id, out var waiting)) waiting.TrySetResult(); }
    }

    private static JsonObject Value(string value, string name, string? description = null) => new() { ["value"] = value, ["name"] = name, ["description"] = description };

    private static JsonArray Options() => new(
        new JsonObject { ["id"] = "mode", ["name"] = "Mode", ["category"] = "mode", ["type"] = "select", ["currentValue"] = "auto", ["options"] = new JsonArray(Value("auto", "Auto", "Runs without asking"), Value("review", "Review", "Asks before it edits")) },
        new JsonObject
        {
            ["id"] = "model", ["name"] = "Model", ["category"] = "model", ["type"] = "select", ["currentValue"] = "m1",
            ["options"] = new JsonArray(
                new JsonObject { ["group"] = "fast", ["name"] = "Fast", ["options"] = new JsonArray(Value("m1", "Mini")) },
                new JsonObject { ["group"] = "smart", ["name"] = "Smart", ["options"] = new JsonArray(Value("m2", "Max", "The best one")) })
        },
        new JsonObject { ["id"] = "thought", ["name"] = "Thought level", ["category"] = "thought_level", ["type"] = "select", ["currentValue"] = "high", ["options"] = new JsonArray(Value("low", "Low"), Value("high", "High")) },
        new JsonObject { ["id"] = "verbose", ["name"] = "Verbose", ["type"] = "boolean", ["currentValue"] = false });

    private JsonObject NewSession(string id)
    {
        var result = new JsonObject { ["sessionId"] = id };
        switch (Offer)
        {
            case AcpOffer.Modes:
                result["modes"] = new JsonObject
                {
                    ["currentModeId"] = "ask",
                    ["availableModes"] = new JsonArray(new JsonObject { ["id"] = "ask", ["name"] = "Ask" }, new JsonObject { ["id"] = "code", ["name"] = "Code" })
                };
                break;
            case AcpOffer.ConfigOptions: result["configOptions"] = configOptions.DeepClone(); break;
            case AcpOffer.Models:
                result["models"] = new JsonObject
                {
                    ["currentModelId"] = "m1",
                    ["availableModels"] = new JsonArray(new JsonObject { ["modelId"] = "m1", ["name"] = "Mini" }, new JsonObject { ["modelId"] = "m2", ["name"] = "Max", ["description"] = "The best one" })
                };
                break;
        }
        return result;
    }

    /// <summary>Changes the current value of an option and tells the client, as an agent does on its own.</summary>
    public async Task ChangeOptionAsync(string session, string configId, JsonNode value)
    {
        foreach (var option in configOptions.OfType<JsonObject>().Where(option => option["id"]!.GetValue<string>() == configId)) option["currentValue"] = value.DeepClone();
        await Update(session, new JsonObject { ["sessionUpdate"] = "config_option_update", ["configOptions"] = configOptions.DeepClone() });
    }

    public Task SetModeUpdateAsync(string session, string mode) => Update(session, new JsonObject { ["sessionUpdate"] = "current_mode_update", ["currentModeId"] = mode });

    private Task Update(string session, JsonObject update) =>
        connection.NotifyAsync("session/update", new JsonObject { ["sessionId"] = session, ["update"] = update });

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private async Task<JsonNode?> HandleAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        lock (Methods) Methods.Add(method);
        switch (method)
        {
            case "initialize":
                ClientCapabilities = parameters?["clientCapabilities"] as JsonObject;
                return new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["agentCapabilities"] = new JsonObject { ["loadSession"] = LoadSession, ["promptCapabilities"] = new JsonObject { ["image"] = true, ["embeddedContext"] = true } },
                    ["authMethods"] = new JsonArray(new JsonObject { ["id"] = "login", ["name"] = "Log in", ["description"] = "Sign in with the agent's own flow." }),
                    ["agentInfo"] = new JsonObject { ["name"] = "fake-agent", ["title"] = "Fake agent", ["version"] = "1.0.0" }
                };
            case "authenticate": return new JsonObject();
            case "session/new": return NewSession("sess-" + Interlocked.Increment(ref sessions));
            case "session/load":
                {
                    var id = parameters!["sessionId"]!.GetValue<string>();
                    await Update(id, new JsonObject { ["sessionUpdate"] = "user_message_chunk", ["content"] = Text("earlier question") });
                    await Update(id, new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = Text("earlier answer") });
                    return new JsonObject();
                }
            case "session/set_mode":
                lock (Last) Last[method] = parameters;
                if (Offer == AcpOffer.ConfigOptions) throw new AcpException(AcpException.MethodNotFound, "session/set_mode is not supported; use session/set_config_option with configId=\"mode\"");
                return new JsonObject();
            case "session/set_model":
                lock (Last) Last[method] = parameters;
                if (Offer != AcpOffer.Models) throw new AcpException(AcpException.MethodNotFound, "session/set_model is not supported");
                return new JsonObject();
            case "session/set_config_option":
                {
                    lock (Last) Last[method] = parameters;
                    var id = parameters!["configId"]!.GetValue<string>();
                    foreach (var option in configOptions.OfType<JsonObject>().Where(option => option["id"]!.GetValue<string>() == id)) option["currentValue"] = parameters["value"]!.DeepClone();
                    return new JsonObject { ["configOptions"] = configOptions.DeepClone() };
                }
            case "session/prompt": return await PromptAsync(parameters!, ct);
            default: throw new AcpException(AcpException.MethodNotFound, "Unknown method " + method);
        }
    }

    private async Task<JsonNode?> PromptAsync(JsonNode parameters, CancellationToken ct)
    {
        var id = parameters["sessionId"]!.GetValue<string>();
        var text = string.Concat((parameters["prompt"] as JsonArray ?? []).Select(block => block?["text"]?.GetValue<string>()));
        JsonObject Stop(string reason) => new() { ["stopReason"] = reason };
        async Task Say(string message) => await Update(id, new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = Text(message) });
        var word = text.Split(' ', 2);
        switch (word[0])
        {
            case "hello":
                await Update(id, new JsonObject { ["sessionUpdate"] = "agent_thought_chunk", ["content"] = Text("Thinking.") });
                await Say("Hi ");
                await Say("there.");
                await Update(id, new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t1", ["title"] = "Edit a.txt", ["kind"] = "edit", ["status"] = "in_progress", ["locations"] = new JsonArray(new JsonObject { ["path"] = "a.txt" }) });
                await Update(id, new JsonObject
                {
                    ["sessionUpdate"] = "tool_call_update",
                    ["toolCallId"] = "t1",
                    ["status"] = "completed",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = "a.txt", ["oldText"] = "old", ["newText"] = "new" })
                });
                await Update(id, new JsonObject
                {
                    ["sessionUpdate"] = "plan",
                    ["entries"] = new JsonArray(new JsonObject { ["content"] = "Step one", ["priority"] = "high", ["status"] = "completed" })
                });
                return Stop("end_turn");
            case "ask":
                {
                    var reply = await connection.RequestAsync("session/request_permission", new JsonObject
                    {
                        ["sessionId"] = id,
                        ["toolCall"] = new JsonObject { ["toolCallId"] = "t2", ["title"] = "Run rm", ["kind"] = "execute", ["status"] = "pending" },
                        ["options"] = new JsonArray(
                            new JsonObject { ["optionId"] = "once", ["name"] = "Allow once", ["kind"] = "allow_once" },
                            new JsonObject { ["optionId"] = "always", ["name"] = "Always allow", ["kind"] = "allow_always" },
                            new JsonObject { ["optionId"] = "no", ["name"] = "Reject", ["kind"] = "reject_once" })
                    }, ct);
                    var outcome = reply?["outcome"];
                    await Say("outcome " + (outcome?["outcome"]?.GetValue<string>() == "selected" ? outcome["optionId"]!.GetValue<string>() : "cancelled"));
                    return Stop("end_turn");
                }
            case "ask2":
                {
                    var reply = await connection.RequestAsync("session/request_permission", new JsonObject
                    {
                        ["sessionId"] = id,
                        ["toolCall"] = new JsonObject
                        {
                            ["toolCallId"] = "t3", ["title"] = "Create file", ["kind"] = "edit", ["status"] = "pending",
                            ["locations"] = new JsonArray(new JsonObject { ["path"] = "/work/a.txt", ["line"] = 3 }),
                            ["rawInput"] = new JsonObject { ["command"] = new JsonArray("echo", "hi"), ["file_path"] = "/work/a.txt" },
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = "/work/a.txt", ["oldText"] = null, ["newText"] = "one\ntwo" })
                        },
                        ["options"] = new JsonArray(
                            new JsonObject { ["optionId"] = "once", ["name"] = "Yes, once", ["kind"] = "allow_once" },
                            new JsonObject { ["optionId"] = "always", ["name"] = "Yes, always", ["kind"] = "allow_always" },
                            new JsonObject { ["optionId"] = "no", ["name"] = "No", ["kind"] = "reject_once" },
                            new JsonObject { ["optionId"] = "never", ["name"] = "Never", ["kind"] = "reject_always" })
                    }, ct);
                    var outcome = reply?["outcome"];
                    await Say("outcome " + (outcome?["outcome"]?.GetValue<string>() == "selected" ? outcome["optionId"]!.GetValue<string>() : "cancelled"));
                    return Stop("end_turn");
                }
            case "read":
                {
                    try
                    {
                        var result = await connection.RequestAsync("fs/read_text_file", new JsonObject { ["sessionId"] = id, ["path"] = word[1] }, ct);
                        await Say("content " + result?["content"]?.GetValue<string>());
                    }
                    catch (AcpException error) { await Say("error " + error.Message); }
                    return Stop("end_turn");
                }
            case "write":
                {
                    try
                    {
                        await connection.RequestAsync("fs/write_text_file", new JsonObject { ["sessionId"] = id, ["path"] = word[1], ["content"] = "written" }, ct);
                        await Say("written");
                    }
                    catch (AcpException error) { await Say("error " + error.Message); }
                    return Stop("end_turn");
                }
            case "run":
                {
                    var created = await connection.RequestAsync("terminal/create", new JsonObject { ["sessionId"] = id, ["command"] = "echo", ["args"] = new JsonArray("hi") }, ct);
                    var terminal = created!["terminalId"]!.GetValue<string>();
                    var exit = await connection.RequestAsync("terminal/wait_for_exit", new JsonObject { ["sessionId"] = id, ["terminalId"] = terminal }, ct);
                    var output = await connection.RequestAsync("terminal/output", new JsonObject { ["sessionId"] = id, ["terminalId"] = terminal }, ct);
                    await connection.RequestAsync("terminal/release", new JsonObject { ["sessionId"] = id, ["terminalId"] = terminal }, ct);
                    await Say($"exit {exit?["exitCode"]} output {output?["output"]?.GetValue<string>()?.Trim()}");
                    return Stop("end_turn");
                }
            case "wait":
                {
                    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (cancels) cancels[id] = cancelled;
                    await Say("working");
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    return Stop("cancelled");
                }
            default:
                await Say("echo " + text);
                return Stop("end_turn");
        }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}