using System.Text.Json.Nodes;

using Avalonia.Headless;
using Avalonia.LogicalTree;

using Tereditor.Plugins.Acp;
using Tereditor.Plugins.Acp.Host;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Chat;

namespace Tereditor.Checks;

// The ACP plugin. Protocol core: framing, version negotiation and a session flow against the in-process fake agent (AcpFakeAgent).
internal static class AcpChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("acp: " + message);
    }

    private sealed class Recorder : IAcpClientHandler
    {
        public readonly List<(string Session, JsonObject Update)> Updates = [];

        public void Update(string sessionId, JsonObject update)
        {
            lock (Updates) Updates.Add((sessionId, update));
        }

        public Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken) =>
            throw new AcpException(AcpException.MethodNotFound, method);

        public string[] Kinds()
        {
            lock (Updates) return [.. Updates.Select(update => update.Update["sessionUpdate"]!.GetValue<string>())];
        }
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("acp: " + message);
            await Task.Delay(10);
        }
    }

    internal static void Manifest()
    {
        var manifest = AcpManifest.Manifest;
        Require(manifest.Id == "acp" && !manifest.EnabledByDefault, "ACP is a builtin that is off by default.");
        Require(manifest.Support == PluginSupportLevels.Early && manifest.Label == "ACP" && manifest.Description!.StartsWith("Early support", StringComparison.Ordinal), "ACP is named ACP and carries the Early support level and description.");
        Require(manifest.UnavailableOn.SequenceEqual([HostKind.PureWeb]), "A host without processes does not offer ACP.");
        Require(ChatContract.Methods.All(method => AcpContract.Contract.Methods.Contains(method)), "ACP serves every chat method.");
    }

    // The logomark: the glyph and the plugin's asset are one drawing, a square monochrome document that parses and draws something.
    // The list and pane control behind Settings > ACP: group headings that cannot be selected, a trailing action and a description on a row, and a width.
    private static void MasterList()
    {
        // The splitter needs a platform; the other modes have set one up by now.
        // Setting up the app makes the dispatcher's context current on this thread; the async cases that follow are waited on here, so they would never resume. Put the old context back.
        if (Avalonia.Application.Current is null)
        {
            var context = SynchronizationContext.Current;
            Avalonia.AppBuilder.Configure<Tereditor.UI.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(context);
        }
        var master = new Tereditor.Plugins.UI.Kit.MasterDetail("Check");
        Require(master.RailWidth == 280, "The list is 280 wide by default.");
        master.RailWidth = 320;
        Require(master.RailWidth == 320, "The list width can be set.");
        var button = new Avalonia.Controls.Button { Name = "CheckInstall", Content = "Install" };
        var rows = new List<Avalonia.Controls.Control>
        {
            Tereditor.Plugins.UI.Kit.MasterDetail.Group("Installed", "Installed"),
            Tereditor.Plugins.UI.Kit.MasterDetail.Row("a", null, "Alpha", "v1", "One line", null, null),
            Tereditor.Plugins.UI.Kit.MasterDetail.Group("Registry", "Registry"),
            Tereditor.Plugins.UI.Kit.MasterDetail.Row("registry:b", null, "Beta", "v2", "Another line", null, button)
        };
        var picked = new List<string?>();
        master.Selected += picked.Add;
        master.SetRows(rows, null);
        Require(master.SelectedId == "a" && rows[0].Tag is null && rows[0].Name == "MasterGroup_Installed", "Headings come first and are never the selected row.");
        master.Rail.SelectedItem = rows[2];
        Require(master.SelectedId == "a" && picked.Count == 0, "Choosing a heading keeps the selected row and says nothing.");
        master.Rail.SelectedItem = rows[3];
        Require(master.SelectedId == "registry:b" && picked.SequenceEqual(["registry:b"]), "Choosing a row reports its id.");
        master.SetRows(rows, "registry:b");
        Require(master.SelectedId == "registry:b" && picked.Count == 1, "Filling the list keeps the selection and says nothing.");
        var texts = rows[3].GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Select(text => text.Text).ToList();
        Require(texts.SequenceEqual(["Beta", "v2", "Another line"]) && rows[3].GetLogicalDescendants().Contains(button), "A row has name, note, description and its action.");
    }

    // The default mode: the host remembers the modes the agent lists, and a new chat switches to the chosen one (Copilot's mode ids are URLs).
    private static async Task DefaultMode()
    {
        var uri = "https://agentclientprotocol.com/protocol/session-modes";
        var seen = new List<ChatMode>();
        var agent = new AcpFakeAgent(offer: AcpOffer.Copilot);
        await using var session = new AcpChatSession("ws", "chat-default-mode", "/work", () => Task.FromResult<IAcpTransport>(new FakeTransport(agent)), _ => { }, _ => { }, null, null, null, uri + "#plan", seen.AddRange);
        session.Begin();
        await session.StartAsync(null);
        Require(session.Header.PermissionMode == uri + "#plan" && agent.Methods.Contains("session/set_config_option"), "A new chat switches to the agent's default mode (Copilot lists its mode as a config option).");
        Require(seen.Select(mode => mode.Label).SequenceEqual(["Agent", "Plan", "Autopilot"]), "The host is told the modes the agent listed.");
        var plain = new AcpFakeAgent(offer: AcpOffer.Copilot);
        await using var untouched = new AcpChatSession("ws", "chat-no-default", "/work", () => Task.FromResult<IAcpTransport>(new FakeTransport(plain)), _ => { }, _ => { }, null, null, null, "no-such-mode");
        untouched.Begin();
        await untouched.StartAsync(null);
        Require(untouched.Header.PermissionMode == uri + "#agent" && !plain.Methods.Contains("session/set_config_option"), "A default the agent does not list is left alone.");
    }

    internal static void Logo()
    {
        var svg = System.Text.Encoding.UTF8.GetString(Tereditor.Plugins.Acp.UI.AcpGlyph.Svg);
        Require(svg.Contains("viewBox=\"0 -178 576 576\"", StringComparison.Ordinal) && svg.Contains("currentColor", StringComparison.Ordinal) && !svg.Contains("<script", StringComparison.OrdinalIgnoreCase) && !svg.Contains("href", StringComparison.OrdinalIgnoreCase), "The ACP logomark is a square, currentColor SVG with no script or reference.");
        using var stream = new MemoryStream(Tereditor.Plugins.Acp.UI.AcpGlyph.Svg);
        using var source = Avalonia.Svg.Skia.SvgSource.LoadFromStream(stream);
        Require(source.Picture is { CullRect: { Width: > 0, Height: > 0 } }, "The ACP logomark parses and draws a non-empty picture.");
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var asset = Path.Combine(directory.FullName, "src", "Tereditor.Plugins.Acp", "assets", "acp.svg");
            if (!File.Exists(asset)) continue;
            Require(File.ReadAllText(asset).Trim() == svg.Trim(), "The plugin's acp.svg is the same drawing as the built-in glyph.");
            break;
        }
    }

    internal static async Task Protocol()
    {
        // Framing and the session flow.
        var handler = new Recorder();
        await using var agent = new AcpFakeAgent();
        await using var client = new AcpClient(agent.ClientInput, agent.ClientOutput, handler);
        var info = await client.InitializeAsync("tereditor", "0.1", CancellationToken.None);
        Require(info is { ProtocolVersion: 1, LoadSession: true, Image: true, EmbeddedContext: true, Audio: false, Name: "Fake agent", Version: "1.0.0" }, "initialize reports the agent's version, capabilities and identity.");
        Require(info.AuthMethods.Single() is { Id: "login", Name: "Log in" }, "Authentication methods are listed, never asked for here.");
        Require(agent.ClientCapabilities!["fs"]!["readTextFile"]!.GetValue<bool>() && agent.ClientCapabilities["terminal"]!.GetValue<bool>(), "The client advertises the file and terminal methods it serves.");

        var session = await client.NewSessionAsync("/work", CancellationToken.None);
        var sessionId = session["sessionId"]!.GetValue<string>();
        Require(sessionId == "sess-1" && session["modes"]!["currentModeId"]!.GetValue<string>() == "ask", "session/new returns the id and the modes.");

        var stop = await client.PromptAsync(sessionId, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hello" }), CancellationToken.None);
        await Until(() => handler.Kinds().Length >= 6, "The streamed updates arrive.");
        Require(stop == "end_turn", "The turn ends with its stop reason.");
        Require(handler.Kinds().SequenceEqual(["agent_thought_chunk", "agent_message_chunk", "agent_message_chunk", "tool_call", "tool_call_update", "plan"]), "Updates arrive in the order the agent sent them.");

        // A message with a line break inside a string is still one frame.
        var multi = await client.PromptAsync(sessionId, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "line one\nline two" }), CancellationToken.None);
        await Until(() => handler.Kinds().Length >= 7, "The multi-line echo arrives.");
        Require(multi == "end_turn" && handler.Updates[^1].Update["content"]!["text"]!.GetValue<string>() == "echo line one\nline two", "A line break inside a string does not split the frame.");

        // Cancel: a notification that ends the pending prompt with `cancelled`.
        var waiting = client.PromptAsync(sessionId, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "wait" }), CancellationToken.None);
        await Until(() => handler.Kinds().Length >= 8, "The long turn starts.");
        await client.CancelAsync(sessionId);
        Require(await waiting == "cancelled", "session/cancel ends the turn with the cancelled stop reason.");

        // A request the client does not serve is answered with method-not-found rather than hanging the agent.
        var loaded = await client.LoadSessionAsync("old", "/work", CancellationToken.None);
        Require(loaded is not null && handler.Updates.Any(update => update.Session == "old" && update.Update["sessionUpdate"]!.GetValue<string>() == "user_message_chunk"), "session/load replays the stored conversation as updates.");

        // The agent dies: pending requests fail instead of hanging.
        var hung = client.PromptAsync(sessionId, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "wait" }), CancellationToken.None);
        await Until(() => handler.Kinds().Count(kind => kind == "agent_message_chunk") >= 6, "The second long turn starts.");
        await agent.KillAsync();
        var failed = false;
        try { await hung; } catch (AcpClosedException) { failed = true; }
        Require(failed, "A pending request fails when the agent's output ends.");
        await client.Closed;

        // Version negotiation.
        await using var future = new AcpFakeAgent(protocolVersion: 2);
        await using var other = new AcpClient(future.ClientInput, future.ClientOutput, new Recorder());
        AcpVersionException? mismatch = null;
        try { await other.InitializeAsync("tereditor", "0.1", CancellationToken.None); } catch (AcpVersionException error) { mismatch = error; }
        Require(mismatch is { AgentVersion: 2 }, "An agent on another protocol version is refused by name.");
    }

    private sealed class FakeTransport(AcpFakeAgent agent) : IAcpTransport
    {
        public Stream FromAgent => agent.ClientInput;
        public Stream ToAgent => agent.ClientOutput;
        public string ErrorTail => "";
        public ValueTask DisposeAsync() => agent.DisposeAsync();
    }

    private static ChatItem[] Items(AcpChatSession session) => [.. session.Snapshot().OfType<ChatItemPush>().Select(push => push.Item)];

    internal static async Task Session()
    {
        var agents = new List<AcpFakeAgent>();
        Func<Task<IAcpTransport>> connect = () =>
        {
            var agent = new AcpFakeAgent();
            agents.Add(agent);
            return Task.FromResult<IAcpTransport>(new FakeTransport(agent));
        };
        await using var session = new AcpChatSession("ws", "chat1", "/work", connect, _ => { }, _ => { });
        Require(session.Begin() is { Status: ChatStatus.Starting, Cwd: "/work" }, "Begin reports a starting chat in its directory.");
        await session.StartAsync(null);
        Require(session.Header is { Status: ChatStatus.Idle, SessionId: "sess-1", PermissionMode: "ask", Title: "Fake agent" } && session.Header.PermissionModes.Select(mode => mode.Key).SequenceEqual(["ask", "code"]), "The session starts idle with the agent's modes and identity.");

        await session.SendAsync("hello", null);
        await Until(() => session.Header.Status == ChatStatus.Idle && Items(session).OfType<ChatAssistantItem>().Any(item => item.Id == "plan"), "A prompt streams a reply and the chat returns to idle.");
        var items = Items(session);
        Require(items.Select(item => item.GetType()).SequenceEqual([typeof(ChatUserItem), typeof(ChatThinkingItem), typeof(ChatAssistantItem), typeof(ChatToolItem), typeof(ChatAssistantItem)]), "A turn folds to the prompt, thought, reply, tool call and plan in order.");
        Require(items.OfType<ChatAssistantItem>().First() is { Text: "Hi there.", Streaming: false }, "Message chunks meet in one finished reply.");
        Require(items.OfType<ChatToolItem>().Single() is { State: ChatToolState.Done, FilePath: "a.txt", Name: "Edit a.txt" } tool && tool.Output!.Contains("-old", StringComparison.Ordinal) && tool.Output.Contains("+new", StringComparison.Ordinal), "The tool card keeps its file and shows the diff.");
        Require(items.OfType<ChatAssistantItem>().Last().Text.Contains("[x] Step one", StringComparison.Ordinal), "The plan shows its entries and their status.");

        await session.SendAsync("ask", null);
        await Until(() => session.Header.Status == ChatStatus.AwaitingPermission, "A permission request waits for the user.");
        var request = Items(session).OfType<ChatPermissionItem>().Single();
        Require(request is { Decision: ChatPermissionDecision.Pending, AlwaysLabel: "Always allow", Title: "Run rm" }, "The request is shown with the agent's own title and its always option.");
        session.Answer(request.RequestId, true, remember: false);
        await Until(() => session.Header.Status == ChatStatus.Idle && Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "outcome once"), "Allow once answers with the allow_once option.");
        Require(Items(session).OfType<ChatPermissionItem>().Single().Decision == ChatPermissionDecision.Allowed, "The card records the decision.");

        await session.SendAsync("ask", null);
        await Until(() => session.Header.Status == ChatStatus.AwaitingPermission, "A second request waits.");
        session.Answer(Items(session).OfType<ChatPermissionItem>().Last().RequestId, false, false);
        await Until(() => Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "outcome no"), "A rejection answers with the reject option.");
        await Until(() => session.Header.Status == ChatStatus.Idle, "The chat is idle after the rejection.");

        await session.SendAsync("ask", null);
        await Until(() => session.Header.Status == ChatStatus.AwaitingPermission, "A third request waits.");
        await session.InterruptAsync();
        await Until(() => Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "outcome cancelled"), "Cancelling answers a waiting permission as cancelled.");
        await Until(() => session.Header.Status == ChatStatus.Idle, "The chat is idle after cancelling a request.");

        await session.SendAsync("wait", null);
        await Until(() => Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "working"), "A long turn starts.");
        await session.InterruptAsync();
        await Until(() => session.Header.Status == ChatStatus.Idle && Items(session).OfType<ChatNoticeItem>().Any(n => n.Category == ChatNoticeKinds.Interrupt), "Interrupt ends the turn with an interrupt marker.");

        // The agent crashes mid-turn: the chat shows it exited, and a new session starts a fresh agent.
        await session.SendAsync("wait", null);
        await Until(() => Items(session).OfType<ChatAssistantItem>().Count(item => item.Text == "working") == 2, "The second long turn starts.");
        await agents[0].KillAsync();
        await Until(() => session.Header.Status == ChatStatus.Exited, "An agent that ends shows the chat as exited.");
        Require(Items(session).OfType<ChatNoticeItem>().Any(n => n.Category == ChatNoticeKinds.Failure), "The exit is explained in a failure notice.");
        Require(!session.Active, "An exited session is not active, so opening the chat again restarts it.");
        await using var restarted = new AcpChatSession("ws", "chat1", "/work", connect, _ => { }, _ => { });
        restarted.Begin();
        await restarted.StartAsync("old-session");
        Require(restarted.Header.Status == ChatStatus.Idle && restarted.SessionId == "old-session" && Items(restarted).OfType<ChatUserItem>().Single().Text == "earlier question" && Items(restarted).OfType<ChatAssistantItem>().Single().Text == "earlier answer", "A restart resumes the stored session through session/load and shows the replayed conversation.");

        // An agent without loadSession starts a new session instead.
        await using var plain = new AcpChatSession("ws", "chat2", "/work", () => Task.FromResult<IAcpTransport>(new FakeTransport(new AcpFakeAgent(loadSession: false))), _ => { }, _ => { });
        plain.Begin();
        await plain.StartAsync("old-session");
        Require(plain.SessionId == "sess-1", "Resume falls back to a new session when the agent cannot load one.");

        // An agent on another protocol version fails the chat with its reason.
        await using var future = new AcpChatSession("ws", "chat3", "/work", () => Task.FromResult<IAcpTransport>(new FakeTransport(new AcpFakeAgent(protocolVersion: 2))), _ => { }, _ => { });
        future.Begin();
        await future.StartAsync(null);
        Require(future.Header is { Status: ChatStatus.Failed, Error: { } reason } && reason.Contains("version 2", StringComparison.Ordinal), "A protocol mismatch fails the chat and says which version the agent speaks.");
    }

    private static async Task<(AcpChatSession Session, AcpFakeAgent Agent)> Started(AcpOffer offer)
    {
        var agent = new AcpFakeAgent(offer: offer);
        var session = new AcpChatSession("ws", "chat-" + offer, "/work", () => Task.FromResult<IAcpTransport>(new FakeTransport(agent)), _ => { }, _ => { });
        session.Begin();
        await session.StartAsync(null);
        return (session, agent);
    }

    // What the agent offers is what the header lists, set through the request that matches how it offered it, and kept in step with its own updates.
    internal static async Task Pickers()
    {
        // Only configOptions: grouped models, a mode, a thought level and a switch. set_mode does not exist, so the mode goes through set_config_option.
        var (config, configAgent) = await Started(AcpOffer.ConfigOptions);
        await using (config)
        {
            var header = config.Header;
            Require(header.Models.Select(model => model.Id).SequenceEqual(["m1", "m2"]) && header.Model == "m1" && header.Models[1] is { Label: "Max", Provider: "Smart", Description: "The best one" }, "Grouped model options are flattened into the model list with their group and description.");
            Require(header.PermissionModes.Select(mode => mode.Key).SequenceEqual(["auto", "review"]) && header.PermissionMode == "auto" && header.PermissionModes[0].Description == "Runs without asking", "A mode option fills the mode list with the agent's names and descriptions.");
            Require(header.Options.Select(option => option.Id).SequenceEqual(["thought", "verbose"]) && header.Options[0] is { Kind: "select", Category: "thought_level", Current: "high" } && header.Options[1] is { Kind: "boolean", Current: "false" }, "The other options are listed with their kind, category and value.");
            Require(configAgent.ClientCapabilities!["session"]!["configOptions"]!["boolean"] is JsonObject, "The client says it handles boolean options.");

            var after = await config.SetModeAsync("review");
            Require(after.PermissionMode == "review" && configAgent.Last["session/set_config_option"]!["configId"]!.GetValue<string>() == "mode" && configAgent.Last["session/set_config_option"]!["value"]!.GetValue<string>() == "review" && !configAgent.Last.ContainsKey("session/set_mode"), "A mode from config options is set with session/set_config_option, never session/set_mode.");
            await config.SetModelAsync("m2");
            Require(config.Header.Model == "m2" && configAgent.Last["session/set_config_option"]!["configId"]!.GetValue<string>() == "model" && !configAgent.Last.ContainsKey("session/set_model"), "A model from config options is set with session/set_config_option.");
            await config.SetOptionAsync("thought", "low");
            Require(config.Header.Options[0].Current == "low" && configAgent.Last["session/set_config_option"]!["configId"]!.GetValue<string>() == "thought", "A select option is set by its id and shows the value the agent answers with.");
            await config.SetOptionAsync("verbose", "true");
            Require(config.Header.Options[1].Current == "true" && configAgent.Last["session/set_config_option"]!["type"]!.GetValue<string>() == "boolean" && configAgent.Last["session/set_config_option"]!["value"]!.GetValue<bool>(), "A switch is set with a JSON boolean and type boolean.");
            await configAgent.ChangeOptionAsync("sess-1", "mode", "auto");
            await Until(() => config.Header.PermissionMode == "auto", "A config_option_update changes the shown mode.");
        }

        // Only modes: set_mode, and current_mode_update. No model, no option.
        var (modes, modesAgent) = await Started(AcpOffer.Modes);
        await using (modes)
        {
            Require(modes.Header.Models.Count == 0 && modes.Header.Model is null && modes.Header.Options.Count == 0, "An agent with only modes lists no model and no option.");
            await modes.SetModeAsync("code");
            Require(modes.Header.PermissionMode == "code" && modesAgent.Last["session/set_mode"]!["modeId"]!.GetValue<string>() == "code", "A mode from the modes list is set with session/set_mode.");
            await modesAgent.SetModeUpdateAsync("sess-1", "ask");
            await Until(() => modes.Header.PermissionMode == "ask", "A current_mode_update changes the shown mode.");
        }

        // Only models: set_model; no mode list.
        var (models, modelsAgent) = await Started(AcpOffer.Models);
        await using (models)
        {
            Require(models.Header.Models.Select(model => model.Label).SequenceEqual(["Mini", "Max"]) && models.Header.Model == "m1" && models.Header.PermissionModes.Count == 0, "An agent with only models lists them and no mode.");
            await models.SetModelAsync("m2");
            Require(models.Header.Model == "m2" && modelsAgent.Last["session/set_model"]!["modelId"]!.GetValue<string>() == "m2", "A model from the models list is set with session/set_model.");
        }

        // GitHub Copilot CLI 1.0.112, as it answered: URL-valued modes, a "permissions" select, no model of any kind, commands sent after the answer.
        const string Uri = "https://agentclientprotocol.com/protocol/session-modes";
        var (copilot, copilotAgent) = await Started(AcpOffer.Copilot);
        await using (copilot)
        {
            var header = copilot.Header;
            Require(copilot.Agent is { Name: "Copilot", Version: "1.0.112", AuthMethods.Count: 1 } && copilot.Agent.AuthMethods[0].Id == "copilot-login", "Copilot's initialize reads, though its sign-in method has no type.");
            Require(header.PermissionModes.Select(mode => mode.Label).SequenceEqual(["Agent", "Plan", "Autopilot"]) && header.PermissionMode == Uri + "#agent" && header.PermissionModes[1].Key == Uri + "#plan", "Copilot's modes show as Agent, Plan and Autopilot with Agent current.");
            Require(header.Models.Count == 0 && header.Model is null, "Copilot offers no model, so none is listed.");
            Require(header.Options.Count == 1 && header.Options[0] is { Id: "allow_all", Label: "Allow All", Category: "permissions", Current: "off" } && header.Options[0].Values.Select(value => value.Label).SequenceEqual(["On", "Off"]), "The permissions select is an option of its own, whatever its category: Allow All, Off.");
            await Until(() => copilot.Header.Commands.Count == 3, "The slash commands sent after the answer still arrive.");
            await copilot.SetModeAsync(Uri + "#plan");
            Require(copilotAgent.Last["session/set_config_option"]!["configId"]!.GetValue<string>() == "mode" && copilotAgent.Last["session/set_config_option"]!["value"]!.GetValue<string>() == Uri + "#plan" && !copilotAgent.Last.ContainsKey("session/set_mode") && copilot.Header.PermissionMode == Uri + "#plan", "Picking Plan sends set_config_option for mode with the whole URL.");
            await copilot.SetOptionAsync("allow_all", "on");
            Require(copilotAgent.Last["session/set_config_option"]!["configId"]!.GetValue<string>() == "allow_all" && copilotAgent.Last["session/set_config_option"]!["value"]!.GetValue<string>() == "on" && copilot.Header.Options[0].Current == "on", "Flipping Allow All sends configId allow_all with value on.");
            await copilotAgent.ChangeOptionAsync("sess-1", "allow_all", "off");
            await Until(() => copilot.Header.Options[0].Current == "off", "A config_option_update turns Allow All off again on the chip.");
            await copilotAgent.ChangeOptionAsync("sess-1", "mode", Uri + "#autopilot");
            await Until(() => copilot.Header.PermissionMode == Uri + "#autopilot", "A config_option_update moves the mode.");
        }

        var (nothing, _) = await Started(AcpOffer.Nothing);
        await using (nothing) Require(nothing.Header is { Models.Count: 0, PermissionModes.Count: 0, Options.Count: 0 }, "An agent that offers nothing gets nothing remembered for it.");
    }

    // A permission request shows what the agent said the call is, and its own answers go back by id.
    internal static async Task Approvals()
    {
        var (session, agent) = await Started(AcpOffer.Modes);
        await using (session)
        {
            await session.SendAsync("ask2", null);
            await Until(() => session.Header.Status == ChatStatus.AwaitingPermission, "The file request waits.");
            var request = Items(session).OfType<ChatPermissionItem>().Single();
            Require(request is { Title: "Create file", ToolKind: "edit", Prompt: "Allow \"Create file\"?" } && request.Locations.SequenceEqual(["/work/a.txt:3"]), "The request keeps the tool's title, kind and file with its line.");
            Require(request.Diffs.Single() is { Path: "/work/a.txt", OldText: null, NewText: "one\ntwo" }, "The request carries the diff of the file it would create.");
            Require(request.Options.Select(option => (option.Id, option.Label, option.Kind)).SequenceEqual([("once", "Yes, once", "allow_once"), ("always", "Yes, always", "allow_always"), ("no", "No", "reject_once"), ("never", "Never", "reject_always")]), "The request lists the agent's options in its order with its names and kinds.");
            Require(Tereditor.Plugins.Chat.UI.PermissionFacts.Of(request.Input).Select(fact => fact.Name + "=" + fact.Value).SequenceEqual(["Command=echo hi", "Path=/work/a.txt"]), "The raw input reads as a command line and a path.");
            session.AnswerOption(request.RequestId, "never");
            await Until(() => Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "outcome never"), "An answer by option id picks that option, even a reject_always the two-button answers never reach.");
            Require(Items(session).OfType<ChatPermissionItem>().Single().Decision == ChatPermissionDecision.Denied, "A reject kind settles the card as denied.");
            await Until(() => session.Header.Status == ChatStatus.Idle, "The turn ends.");
            await session.SendAsync("ask2", null);
            await Until(() => session.Header.Status == ChatStatus.AwaitingPermission, "A second file request waits.");
            var refused = false;
            try { session.AnswerOption(Items(session).OfType<ChatPermissionItem>().Last().RequestId, "made-up"); } catch (InvalidOperationException) { refused = true; }
            Require(refused, "An option id the agent did not offer is refused.");
            session.AnswerOption(Items(session).OfType<ChatPermissionItem>().Last().RequestId, "always");
            await Until(() => Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "outcome always"), "allow_always answers with its option.");
        }
        Require(Tereditor.Plugins.Chat.UI.PermissionFacts.Unified(null, "a\nb") == "+ a\n+ b" && Tereditor.Plugins.Chat.UI.PermissionFacts.Unified("a\nb\nc\nd", "a\nb\nX\nd") == "  a\n  b\n- c\n+ X\n  d", "A short diff shows only the changed lines and their neighbours.");
    }

    // The agent's file and terminal calls through a session whose workspace is a temporary directory: reads and writes stay inside it.
    internal static async Task ClientMethods(string root)
    {
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "one\ntwo\nthree");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(workspace, "escape");
        if (!OperatingSystem.IsWindows() && !Directory.Exists(link)) Directory.CreateSymbolicLink(link, outside);

        var methods = new AcpClientMethods(workspace);
        await using var session = new AcpChatSession("ws", "chat1", workspace, () => Task.FromResult<IAcpTransport>(new FakeTransport(new AcpFakeAgent())), _ => { }, _ => { }, methods);
        session.Begin();
        await session.StartAsync(null);

        async Task<string> Ask(string prompt, string expected)
        {
            var before = Items(session).OfType<ChatAssistantItem>().Count();
            await session.SendAsync(prompt, null);
            await Until(() => session.Header.Status == ChatStatus.Idle && Items(session).OfType<ChatAssistantItem>().Count() > before, expected);
            return Items(session).OfType<ChatAssistantItem>().Last().Text;
        }

        Require(await Ask("read " + Path.Combine(workspace, "a.txt"), "The read answers.") == "content one\ntwo\nthree", "The agent reads a workspace file.");
        Require(await Ask("read a.txt", "The relative read answers.") == "content one\ntwo\nthree", "A relative path is taken against the workspace.");
        Require((await Ask("read " + Path.Combine(outside, "secret.txt"), "The outside read answers.")).StartsWith("error", StringComparison.Ordinal), "A file outside the workspace is refused.");
        Require((await Ask("read ../outside/secret.txt", "The dotdot read answers.")).Contains("outside the workspace", StringComparison.Ordinal), "A path that climbs out of the workspace is refused.");
        if (!OperatingSystem.IsWindows())
            Require((await Ask("read " + Path.Combine(link, "secret.txt"), "The link read answers.")).Contains("outside the workspace", StringComparison.Ordinal), "A symbolic link inside the workspace cannot lead out of it.");
        Require(await Ask("write " + Path.Combine(workspace, "new", "b.txt"), "The write answers.") == "written" && File.ReadAllText(Path.Combine(workspace, "new", "b.txt")) == "written", "The agent writes a file inside the workspace.");
        Require((await Ask("write " + Path.Combine(outside, "evil.txt"), "The outside write answers.")).StartsWith("error", StringComparison.Ordinal) && !File.Exists(Path.Combine(outside, "evil.txt")), "A write outside the workspace is refused and writes nothing.");
        if (!OperatingSystem.IsWindows())
            Require(await Ask("run", "The terminal answers.") == "exit 0 output hi", "A terminal runs the command and reports its exit and output.");

        var direct = new JsonObject { ["path"] = "a.txt", ["line"] = 2, ["limit"] = 1 };
        Require((await methods.HandleAsync("fs/read_text_file", direct, CancellationToken.None))!["content"]!.GetValue<string>() == "two", "line and limit select part of the file.");
        AcpException? refused = null;
        try { await methods.HandleAsync("terminal/create", new JsonObject { ["command"] = "echo", ["cwd"] = outside }, CancellationToken.None); } catch (AcpException error) { refused = error; }
        Require(refused is not null, "A terminal cannot start outside the workspace.");
        try { await methods.HandleAsync("session/unknown", new JsonObject(), CancellationToken.None); refused = null; } catch (AcpException error) { refused = error; }
        Require(refused is { Code: AcpException.MethodNotFound }, "An unknown client method is answered as not found.");
    }

    private static string FixtureText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp-registry.json"));

    // The registry: the captured real JSON (trimmed), its cache with ETag revalidation and offline fallback, and the install confirmation.
    internal static async Task Registry(string root)
    {
        const string mac = "darwin-aarch64";
        Directory.CreateDirectory(root);
        var fixture = AcpRegistry.Parse(FixtureText()) ?? throw new InvalidOperationException("acp: The captured registry does not parse.");
        var view = AcpRegistry.View(fixture, "network", null, null, mac);
        Require(view.SchemaVersion == "1.0.0" && view.Agents.Select(agent => agent.Id).SequenceEqual(["codex-acp", "fast-agent", "goose", "claude-acp", "pi-acp"]), "The captured registry lists its agents by name.");
        var goose = view.Agents.Single(agent => agent.Id == "goose");
        Require(goose is { Kinds: ["binary"], Installable: true, Version: "1.57.0", License: "Apache-2.0" } && view.Agents.Single(agent => agent.Id == "pi-acp").Kinds.SequenceEqual(["npx"]) && view.Agents.Single(agent => agent.Id == "fast-agent").Kinds.SequenceEqual(["uvx"]), "Each agent shows its distribution kinds.");
        Require(view.Agents.Single(agent => agent.Id == "pi-acp").Icon!.EndsWith("pi-acp.svg", StringComparison.Ordinal), "The registry's icon url is kept.");
        Require(!AcpRegistry.View(fixture, "x", null, null, "plan9-riscv").Agents.Single(agent => agent.Id == "goose").Installable, "A binary-only agent is not installable on a platform it has no archive for.");

        Require(view.Agents.Where(agent => agent.NotRecommended is not null).Select(agent => agent.Id).SequenceEqual(["claude-acp", "pi-acp"]) && view.Agents.Single(agent => agent.Id == "goose").NotRecommended is null && view.Agents.Single(agent => agent.Id == "codex-acp").NotRecommended is null, "Only the Claude and Pi adapters are flagged, and they sort last.");
        Require(AcpRegistry.Plan(fixture, "claude-acp", null, mac)!.NotRecommended is { Length: > 0 } && AcpRegistry.Plan(fixture, "pi-acp", null, mac)!.NotRecommended!.StartsWith("Pi ", StringComparison.Ordinal) && AcpRegistry.Plan(fixture, "goose", null, mac)!.NotRecommended is null, "The install confirmation carries the not-recommended flag.");
        var custom = AcpRegistry.View(new JsonObject { ["agents"] = new JsonArray(new JsonObject { ["id"] = "my-claude", ["name"] = "Claude", ["version"] = "1", ["description"] = "mine", ["distribution"] = new JsonObject { ["npx"] = new JsonObject { ["package"] = "x" } } }) }, "x", null, null, mac);
        Require(custom.Agents.Single().NotRecommended is null, "Matching is by exact id, so another agent named Claude is not flagged.");
        var npx = AcpRegistry.Plan(fixture, "pi-acp", null, mac)!;
        Require(npx is { Kind: "npx", CommandLine: "bunx pi-acp@0.0.34", Version: "0.0.34" }, "An npx plan shows the exact command that will run.");
        var uvx = AcpRegistry.Plan(fixture, "fast-agent", null, mac)!;
        Require(uvx.CommandLine == "uvx fast-agent-acp==0.10.1 -x", "A uvx plan shows its package pin and arguments.");
        var binary = AcpRegistry.Plan(fixture, "goose", null, mac)!;
        Require(binary is { Kind: "binary", Sha256.Length: 64, CommandLine: "./goose acp" } && binary.Source.EndsWith("goose-aarch64-apple-darwin.tar.bz2", StringComparison.Ordinal), "A binary plan shows the archive url, checksum and command.");
        Require(AcpRegistry.Plan(fixture, "goose", null, "plan9-riscv") is null && AcpRegistry.Plan(fixture, "nope", null, mac) is null, "No plan for an agent or platform the registry does not have.");

        // Cache: first run fetches, a fresh copy is not refetched, a refresh revalidates with the ETag, offline falls back to the copy.
        var directory = Path.Combine(root, "registry-cache");
        var calls = new List<(string? ETag, string? Modified)>();
        var online = true;
        var clock = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        AcpFetch fetch = (url, etag, modified, _) =>
        {
            calls.Add((etag, modified));
            if (!online) throw new HttpRequestException("no network");
            return Task.FromResult(etag == "\"v1\"" ? new AcpFetchResult(304, null, etag, modified) : new AcpFetchResult(200, FixtureText(), "\"v1\"", "Wed, 04 Nov 2026 12:58:12 GMT"));
        };
        var cache = new AcpRegistryCache(directory, fetch, () => clock);
        var (_, first) = await cache.LoadAsync(false, CancellationToken.None);
        Require(first is { Source: "network", Note: null } && calls.Count == 1 && calls[0] == (null, null), "The first run fetches the registry without conditions.");
        clock = clock.AddMinutes(1);
        var (_, again) = await cache.LoadAsync(false, CancellationToken.None);
        Require(again.Source == "cache" && calls.Count == 1, "A copy younger than the registry's cache lifetime is not fetched again.");
        clock = clock.AddMinutes(10);
        var (_, revalidated) = await cache.LoadAsync(false, CancellationToken.None);
        Require(revalidated.Source == "network" && calls.Count == 2 && calls[1] == ("\"v1\"", "Wed, 04 Nov 2026 12:58:12 GMT"), "A stale copy is revalidated with If-None-Match and If-Modified-Since, and a 304 keeps it.");
        online = false;
        clock = clock.AddHours(1);
        var (_, offline) = await cache.LoadAsync(false, CancellationToken.None);
        Require(offline is { Source: "cache", Note: { } note } && note.Contains("earlier", StringComparison.Ordinal) && offline.Agents.Count == 5, "Offline, the cached copy is shown with a note.");
        var (_, seeded) = await new AcpRegistryCache(Path.Combine(root, "registry-empty"), fetch, () => clock).LoadAsync(false, CancellationToken.None);
        Require(seeded is { Source: "seed", Note: { } } && seeded.Agents.Count == 2, "With no copy and no network a built-in minimal list stands in, and says so.");
        online = true;
        var (_, garbage) = await new AcpRegistryCache(Path.Combine(root, "registry-bad"), (_, _, _, _) => Task.FromResult(new AcpFetchResult(200, "<html>captive portal</html>", null, null)), () => clock).LoadAsync(false, CancellationToken.None);
        Require(garbage.Source == "seed", "An answer that is not a registry is never cached or shown.");

        // Install: nothing happens without the confirmation of exactly this version; a bad checksum discards the download.
        var downloads = new List<string>();
        var archive = Path.Combine(root, "pkg.tar.gz");
        var source = Path.Combine(root, "pkg");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "goose"), "#!/bin/sh\nexit 0\n");
        System.Formats.Tar.TarFile.CreateFromDirectory(source, Path.Combine(root, "pkg.tar"), false);
        await using (var raw = File.OpenRead(Path.Combine(root, "pkg.tar")))
        await using (var zipped = File.Create(archive))
        await using (var gzip = new System.IO.Compression.GZipStream(zipped, System.IO.Compression.CompressionLevel.Fastest)) await raw.CopyToAsync(gzip);
        var bytes = File.ReadAllBytes(archive);
        var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var local = (JsonObject)fixture.DeepClone();
        var target = (JsonObject)local["agents"]!.AsArray().OfType<JsonObject>().First(agent => agent["id"]!.GetValue<string>() == "goose")["distribution"]!["binary"]![mac]!;
        target["archive"] = "https://example.invalid/goose.tar.gz";
        target["sha256"] = good;
        var installer = new AcpInstaller(Path.Combine(root, "agents"), (url, _) => { downloads.Add(url); return Task.FromResult(bytes); });

        async Task<string> Try(AcpInstallRequest request)
        {
            try { await installer.InstallAsync(local, request, mac, CancellationToken.None); return ""; }
            catch (InvalidOperationException error) { return error.Message; }
        }

        Require((await Try(new("goose", "binary", "1.57.0", false))).Contains("confirmation", StringComparison.Ordinal) && downloads.Count == 0, "Without a confirmation nothing is downloaded.");
        Require((await Try(new("goose", "binary", "1.0.0", true))).Contains("review", StringComparison.Ordinal) && downloads.Count == 0, "A confirmation for another version than the registry offers installs nothing.");
        target["sha256"] = new string('0', 64);
        Require((await Try(new("goose", "binary", "1.57.0", true))).Contains("checksum", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(root, "agents", "goose", "1.57.0")), "A download that fails its checksum is discarded and nothing is unpacked.");
        target["sha256"] = good;
        if (!OperatingSystem.IsWindows())
        {
            var entry = await installer.InstallAsync(local, new("goose", "binary", "1.57.0", true), mac, CancellationToken.None);
            Require(entry is { Origin: "registry", Version: "1.57.0" } && File.Exists(entry.Command) && entry.Command.StartsWith(Path.Combine(root, "agents"), StringComparison.Ordinal) && entry.Args.SequenceEqual(["acp"]), "A confirmed, verified binary is unpacked under the agents directory and becomes an entry.");
        }
        var package = await installer.InstallAsync(local, new("pi-acp", "npx", "0.0.34", true), mac, CancellationToken.None);
        Require(package is { Command: "npx", Version: "0.0.34" } && package.Args.SequenceEqual(["-y", "pi-acp@0.0.34"]), "An npx install becomes an entry that runs the pinned package.");
    }

    // The settings page's plain-text forms for arguments and environment.
    internal static void EntryText()
    {
        Require(AcpEntryText.SplitArguments("--flag \"two words\" 'x y' plain").SequenceEqual(["--flag", "two words", "x y", "plain"]), "Quotes keep a space inside one argument.");
        var env = AcpEntryText.ParseEnvironment("A=1\n\nB=x=y\n=bad\n  C = 3 ");
        Require(env.Count == 3 && env["A"] == "1" && env["B"] == "x=y" && env["C"] == " 3", "Environment lines are NAME=value; the value keeps everything after the first equals sign.");
        Require(AcpEntryText.FormatEnvironment(env).Split('\n').Length == 3, "Environment round-trips to lines.");
        Require(AcpEntryText.CommandLine(new AcpAgentEntry("x", "X", "/a b/agent") { Args = ["--c", "d e"] }) == "\"/a b/agent\" --c \"d e\"", "The shown command line quotes parts with spaces.");
        Require(AcpEntryText.IdFor("My Agent!") == "custom-my-agent", "A custom agent gets a stable id from its name.");
    }

    // The settings lists mirror Code Intelligence: one row model per registry entry, a search over name, description and id, and an install status per row.
    internal static void Rows()
    {
        var view = AcpRegistry.View(AcpRegistry.Parse(FixtureText())!, "network", null, null, "darwin-aarch64");
        var none = new List<AcpAgentEntry>();
        Require(Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, none, null).Count == 5 && Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, none, "  ").Count == 5, "A blank search lists every registry agent.");
        Require(Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, none, "GOOSE").Select(row => row.Agent.Id).SequenceEqual(["goose"]) && Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, none, "nothing like it").Count == 0, "The search matches name, description and id without case.");
        var rows = Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, [new AcpAgentEntry("goose", "goose", "x") { Origin = "registry", Version = "1.57.0" }, new AcpAgentEntry("pi-acp", "Pi", "npx") { Origin = "registry", Version = "0.0.1" }, new AcpAgentEntry("codex-acp", "mine", "x")], null);
        Require(rows.Single(row => row.Agent.Id == "goose") is { Status: "installed", CanInstall: false, ActionLabel: "Installed" }, "An installed agent of the same version says Installed and offers no button.");
        Require(rows.Single(row => row.Agent.Id == "pi-acp") is { Status: "update", CanInstall: true, ActionLabel: "Update ▷" }, "Another installed version offers an update.");
        Require(rows.Single(row => row.Agent.Id == "codex-acp") is { Status: "available", ActionLabel: "Install ▷" }, "A custom agent with the same id is not the registry install.");
        var plan9 = AcpRegistry.View(AcpRegistry.Parse(FixtureText())!, "network", null, null, "plan9-riscv");
        Require(Tereditor.Plugins.Acp.UI.AcpRows.Registry(plan9, none, "goose").Single() is { Status: "unavailable", CanInstall: false }, "An agent with no distribution for this machine cannot be installed.");
    }

    // Settings > ACP shows one list: installed agents pinned on top, then the registry agents that are not installed.
    internal static void CombinedList()
    {
        var view = AcpRegistry.View(AcpRegistry.Parse(FixtureText())!, "network", null, null, "darwin-aarch64");
        var entries = new List<AcpAgentEntry>
        {
            new("pi-acp", "Pi", "npx") { Origin = "registry", Version = "0.0.1" },
            new("goose", "goose", "/x/goose") { Origin = "registry", Version = "1.57.0" },
            new("custom-zed", "Zed", "/bin/zed")
        };
        var none = new Dictionary<string, string>();
        var all = Tereditor.Plugins.Acp.UI.AcpRows.Combined(entries, view, none, "", null);
        Require(all.Installed.Select(row => row.Entry.Id).SequenceEqual(["goose", "pi-acp", "custom-zed"]), "Installed agents come first, by name, registry installs and custom ones together.");
        Require(all.Available.Count == 3 && all.Available.All(row => row.Agent.Id is not ("goose" or "pi-acp")), "An installed registry agent is not repeated in the registry group.");
        Require(all.Installed.Select(row => row.Entry.Id).Concat(all.Available.Select(row => row.Agent.Id)).Distinct().Count() == all.Installed.Count + all.Available.Count, "No agent appears twice in the list.");
        Require(all.Installed.Single(row => row.Entry.Id == "custom-zed").Tags[0] == "Custom", "A custom agent is marked Custom.");
        var filtered = Tereditor.Plugins.Acp.UI.AcpRows.Combined(entries, view, none, "", "GOOSE");
        Require(filtered.Installed.Select(row => row.Entry.Id).SequenceEqual(["goose"]) && filtered.Available.Count == 0, "The search filters the installed group and the registry group, without bringing an installed agent back below.");
        var zed = Tereditor.Plugins.Acp.UI.AcpRows.Combined(entries, view, none, "", "zed");
        Require(zed.Installed.Select(row => row.Entry.Id).SequenceEqual(["custom-zed"]), "The search matches a custom agent by name.");
        var offline = Tereditor.Plugins.Acp.UI.AcpRows.Combined(entries, null, none, "", null);
        Require(offline.Installed.Count == 3 && offline.Available.Count == 0, "Without the registry list the installed agents still show.");
    }

    // Settings > ACP is a list of every agent (registry installs and custom ones together) with a pane for the selected one.
    internal static void AgentList()
    {
        var view = AcpRegistry.View(AcpRegistry.Parse(FixtureText())!, "network", null, null, "darwin-aarch64");
        var entries = new List<AcpAgentEntry>
        {
            new("pi-acp", "Pi", "npx") { Origin = "registry", Version = "0.0.1", Args = ["-y", "pi-acp@0.0.1"], Description = "pi" },
            new("goose", "goose", "/x/goose") { Origin = "registry", Version = "1.57.0", Source = "https://example.invalid/goose.tar.bz2", License = "Apache-2.0", Website = "https://goose.example/" },
            new("custom-zed", "Zed", "/bin/zed") { Enabled = false },
            new("codex-acp", "Codex", "/x/codex") { Origin = "registry", Version = "2.5.0" }
        };
        var problems = new Dictionary<string, string> { ["goose"] = "The program is missing: /x/goose" };
        var rows = Tereditor.Plugins.Acp.UI.AcpRows.Agents(entries, view, problems, "goose");
        Require(rows.Select(row => row.Entry.Id).SequenceEqual(new[] { "codex-acp", "goose", "pi-acp", "custom-zed" }), "Registry installs and custom agents are listed together by name.");
        Require(rows.Single(row => row.Entry.Id == "pi-acp").Tags.SequenceEqual(["Registry", "Not recommended", "Update available"]), "A registry install shows Registry, Not recommended and Update available.");
        Require(rows.Single(row => row.Entry.Id == "custom-zed").Tags.SequenceEqual(["Custom", "Off"]) && rows.Single(row => row.Entry.Id == "goose").Tags.SequenceEqual(["Registry", "Default"]) && rows.Single(row => row.Entry.Id == "codex-acp").Tags.SequenceEqual(["Registry"]), "Custom agents say Custom, a turned-off agent says Off, the default says Default, a current install has no extra tag.");
        Require(rows.Where(row => row.HasDot).Select(row => row.Entry.Id).SequenceEqual(["goose"]) && rows.Single(row => row.HasDot).Problem!.Contains("missing", StringComparison.Ordinal), "Only an agent with a problem gets a status dot, and the problem is its tooltip.");
        Require(Tereditor.Plugins.Acp.UI.AcpRows.Agents(entries, null, problems, "").Single(row => row.Entry.Id == "pi-acp").Tags.SequenceEqual(["Registry"]), "Without the registry list there is no Not recommended or Update available tag.");

        var detail = new Tereditor.Plugins.Acp.UI.AcpAgentDetail(entries[1], Tereditor.Plugins.Acp.UI.AcpRows.RegistryAgent(entries[1], view), problems["goose"]);
        Require(detail is { IsRegistry: true, License: "Apache-2.0", Website: "https://goose.example/", CanReinstall: true, CanUpdate: false, UpdateTo: null } && detail.Version == "Version 1.57.0" && detail.SourceLine.Contains("example.invalid", StringComparison.Ordinal) && detail.InstallStatus.Contains("missing", StringComparison.Ordinal) && detail.Location == "/x/goose", "The detail of a registry agent shows its version, source, links, license, status and where the program is.");
        var pi = new Tereditor.Plugins.Acp.UI.AcpAgentDetail(entries[0], Tereditor.Plugins.Acp.UI.AcpRows.RegistryAgent(entries[0], view), null);
        Require(pi is { CanUpdate: true, CanReinstall: false, UpdateTo: "0.0.34" } && pi.InstallStatus.Contains("0.0.34", StringComparison.Ordinal) && pi.Location.StartsWith("bunx ", StringComparison.Ordinal), "An older install offers an update, and an npx agent says what runs it.");
        var custom = new Tereditor.Plugins.Acp.UI.AcpAgentDetail(entries[2], null, null);
        Require(custom is { IsRegistry: false, CanUpdate: false, CanReinstall: false, InstallStatus: "Custom" } && custom.SourceLine == "Added by you" && custom.CommandLine == "/bin/zed", "A custom agent has no update or reinstall.");

        var overridden = entries[1] with { CommandOverride = "/other/goose", UseDefaultArgs = false, ArgsOverride = ["acp", "--fast"], Args = ["acp"] };
        Require(overridden.LaunchCommand() == "/other/goose" && overridden.LaunchArgs().SequenceEqual(["acp", "--fast"]) && entries[1].LaunchCommand() == "/x/goose" && entries[1].LaunchArgs().Count == 0, "An executable override and own arguments replace the registry's; without them the registry's are used.");
        Require((entries[2] with { UseDefaultArgs = false, ArgsOverride = ["x"], Args = ["own"] }).LaunchArgs().SequenceEqual(["own"]), "A custom agent's own arguments are always used.");
        Require(AcpEntryText.ParseLines("a\r\n\n b c \n").SequenceEqual(["a", " b c "]), "Arguments are one per line, blank lines dropped.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("custom-zed", [entries[2], entries[3]]) is { Kind: "start", Agent.Id: "codex-acp" } && Tereditor.Plugins.Acp.UI.AcpLauncher.MenuEntries("", entries).All(entry => entry.Agent.Enabled), "A turned-off agent is not started from the button, even as the default.");
    }

    // The Add custom agent popup validates its form before anything is saved.
    internal static void CustomForm()
    {
        var existing = new List<AcpAgentEntry> { new("custom-mine", "Mine", "/bin/mine") };
        var ok = Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate(" My Agent ", " /usr/bin/agent ", "--flag \"two words\"", "A=1\nB=x=y", "/work", existing);
        Require(ok.Entry is { Id: "custom-my-agent", Name: "My Agent", Command: "/usr/bin/agent", Cwd: "/work", Origin: "custom", Enabled: true } entry && entry.Args.SequenceEqual(["--flag", "two words"]) && entry.Env.Count == 2 && ok.Errors.Count == 0, "A valid form becomes a custom entry with the same fields as before.");
        var none = Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("", "  ", null, null, null, existing);
        Require(none.Entry is null && none.Errors.Count == 2, "A name and a command are needed.");
        Require(Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("mine", "x", "", "", "", existing).Errors.Single().Contains("already", StringComparison.Ordinal) && Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("MINE!", "x", "", "", "", existing).Errors.Count == 1, "A name already in the list is refused, so adding never replaces another agent.");
        var bad = Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("n", "x", "", "A=1\nnot a pair\n=x", "relative/dir", existing);
        Require(bad.Entry is null && bad.Errors.Count == 3 && bad.Errors[0].Contains("line 2", StringComparison.Ordinal) && bad.Errors[1].Contains("line 3", StringComparison.Ordinal), "Environment lines that are not NAME=value and a relative working directory are refused, each with its reason.");
        Require(Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("n", "x\ny", "", "", "", existing).Errors.Count == 1, "The command is one line.");
    }

    // What the registry ships to verify (checked against the live registry on 2026-11-06: an optional sha256 per binary target, no signature of any kind, every npx and uvx package pinned), and what is done with it.
    internal static async Task Verification(string root)
    {
        Directory.CreateDirectory(root);
        const string mac = "darwin-aarch64";
        Require(AcpRegistry.IsPinned("npx", "pi-acp@0.0.34") && AcpRegistry.IsPinned("npx", "@scope/name@1.2.3-beta.1") && AcpRegistry.IsPinned("uvx", "fast-agent-acp==0.10.1"), "Exact versions are pinned.");
        Require(!AcpRegistry.IsPinned("npx", "pi-acp") && !AcpRegistry.IsPinned("npx", "@scope/name") && !AcpRegistry.IsPinned("npx", "pi-acp@latest") && !AcpRegistry.IsPinned("npx", "pi-acp@^1.2.0") && !AcpRegistry.IsPinned("uvx", "x>=1") && !AcpRegistry.IsPinned("uvx", "x"), "A tag, a range or no version is not pinned.");
        Require(AcpRegistry.IsSha256(new string('a', 64)) && !AcpRegistry.IsSha256(new string('a', 63)) && !AcpRegistry.IsSha256(new string('g', 64)) && !AcpRegistry.IsSha256(null), "Only 64 hex digits are a SHA-256.");

        var fixture = AcpRegistry.Parse(FixtureText())!;
        Require(AcpRegistry.Plan(fixture, "goose", null, mac)!.Verification == AcpRegistry.Sha256Verified && AcpRegistry.Plan(fixture, "pi-acp", null, mac)!.Verification == AcpRegistry.Pinned && AcpRegistry.Plan(fixture, "fast-agent", null, mac)!.Verification == AcpRegistry.Pinned, "The plan says what will be verified: SHA-256, or a pinned version.");
        var runPlan = AcpRegistry.Plan(fixture, "pi-acp", null, mac)!;
        Require(runPlan.CommandLine.StartsWith("bunx pi-acp@0.0.34", StringComparison.Ordinal) && runPlan.Verification == AcpRegistry.Pinned && !runPlan.CommandLine.Contains("npx", StringComparison.Ordinal), "An npx agent's plan runs with Bun and keeps its pin verification.");
        var (runner, runnerArgs) = Tereditor.Plugins.Acp.Host.AcpRunner.Bun("/opt/bun", ["-y", "@scope/pi-acp@0.0.34", "--acp"]);
        Require(runner == "/opt/bun" && runnerArgs.SequenceEqual(["x", "@scope/pi-acp@0.0.34", "--acp"]) && Tereditor.Plugins.Acp.Host.AcpRunner.Bun("b", ["pi@1", "-y"]).Args.SequenceEqual(["x", "pi@1", "-y"]), "The npx arguments did not become the same package run by bun x.");
        var guardHome = Path.Combine(root, "bun-guard");
        var guarded = Tereditor.Plugins.Acp.Host.AcpRunner.GuardedEnvironment(guardHome, new Dictionary<string, string> { ["TOKEN"] = "t", ["DO_NOT_TRACK"] = "0" });
        Require(guarded["BUN_OPTIONS"].Contains("--no-env-file", StringComparison.Ordinal) && guarded["BUN_OPTIONS"].Contains(Path.Combine(guardHome, "empty-bunfig.toml"), StringComparison.Ordinal) && new FileInfo(Path.Combine(guardHome, "empty-bunfig.toml")).Length == 0 && guarded["BUN_INSTALL"] == guardHome && guarded["TOKEN"] == "t" && guarded["DO_NOT_TRACK"] == "0", "Bun was not kept from the workspace's bunfig and .env, or the agent's own environment was lost.");
        var loose = (JsonObject)fixture.DeepClone();
        loose["agents"]!.AsArray().OfType<JsonObject>().First(agent => agent["id"]!.GetValue<string>() == "pi-acp")["distribution"]!["npx"]!["package"] = "pi-acp";
        var goose = (JsonObject)loose["agents"]!.AsArray().OfType<JsonObject>().First(agent => agent["id"]!.GetValue<string>() == "goose")["distribution"]!["binary"]![mac]!;
        Require(AcpRegistry.Plan(loose, "pi-acp", null, mac)!.Verification == AcpRegistry.NotPinned, "A package without an exact version says Not pinned.");
        var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3])).ToLowerInvariant();
        goose["archive"] = "https://example.invalid/goose";
        goose["cmd"] = "goose";
        goose["sha256"] = new string('0', 64);
        var downloads = 0;
        var installer = new AcpInstaller(Path.Combine(root, "agents"), (_, _) => { downloads++; return Task.FromResult(new byte[] { 1, 2, 3 }); });
        async Task<string> Try()
        {
            try { await installer.InstallAsync(loose, new("goose", "binary", "1.57.0", true), mac, CancellationToken.None); return ""; }
            catch (InvalidOperationException error) { return error.Message; }
        }
        var mismatch = await Try();
        Require(mismatch.Contains("does not match", StringComparison.Ordinal) && mismatch.Contains("nothing was installed", StringComparison.Ordinal) && downloads == 1 && !Directory.Exists(Path.Combine(root, "agents", "goose")), "A checksum mismatch refuses the install after the download and before anything is written.");
        goose["sha256"] = "not-a-checksum";
        downloads = 0;
        Require((await Try()).Contains("not a valid SHA-256", StringComparison.Ordinal) && downloads == 0, "A checksum that is not a SHA-256 is refused before the download, not skipped.");
        goose["sha256"] = good.ToUpperInvariant();
        goose["archive"] = "http://example.invalid/goose";
        Require((await Try()).Contains("not https", StringComparison.Ordinal) && downloads == 0, "A download that is not https is refused.");
        goose["archive"] = "https://example.invalid/goose";
        var verified = await installer.InstallAsync(loose, new("goose", "binary", "1.57.0", true), mac, CancellationToken.None);
        Require(verified.Verification == AcpRegistry.Sha256Verified && verified.Source == "https://example.invalid/goose", "A matching checksum (in either letter case) installs and the entry records SHA-256 verified.");
        goose.Remove("sha256");
        var unchecked_ = await installer.InstallAsync(loose, new("goose", "binary", "1.57.0", true), mac, CancellationToken.None);
        Require(unchecked_.Verification == AcpRegistry.NoChecksum && AcpRegistry.Plan(loose, "goose", null, mac)!.Verification == AcpRegistry.NoChecksum, "With no checksum the install goes ahead as before and says so, in the plan and on the entry.");
        var pinned = await installer.InstallAsync(fixture, new("pi-acp", "npx", "0.0.34", true), mac, CancellationToken.None);
        Require(pinned.Verification == AcpRegistry.Pinned, "A pinned package records Version pinned.");

        var plan = AcpRegistry.Plan(fixture, "goose", null, mac)!;
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Explanation(plan).Contains(AcpInstallPlan.TrustNote, StringComparison.Ordinal) && AcpInstallPlan.TrustNote.Contains("only install ones you trust", StringComparison.Ordinal) && Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Facts(plan).Single(fact => fact.Label == "Checked").Value == AcpRegistry.Sha256Verified, "The install confirmation carries the trust notice and what is verified.");
        Require(AcpInstallPlan.TrustNote.Contains("third-party programs", StringComparison.OrdinalIgnoreCase) && !AcpInstallPlan.TrustNote.Contains("checksum", StringComparison.OrdinalIgnoreCase) && !AcpInstallPlan.TrustNote.Contains('—'), "The trust notice says registry agents are third-party programs to trust, without explaining checksums.");
        var detail = new Tereditor.Plugins.Acp.UI.AcpAgentDetail(verified, null, null);
        Require(detail.Verification == AcpRegistry.Sha256Verified && new Tereditor.Plugins.Acp.UI.AcpAgentDetail(new AcpAgentEntry("a", "A", "x") { Origin = "registry" }, null, null).Verification.StartsWith("Not recorded", StringComparison.Ordinal), "The detail pane shows what was verified, or that an older install has no record.");
    }

    private const string GoodIcon = """<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16"><defs><linearGradient id="g"><stop offset="0" stop-color="#fff"/></linearGradient></defs><path fill="currentColor" d="M0 0h16v16H0z"/><circle r="3" fill="url(#g)" stroke="#123456"/></svg>""";

    // Registry icons: untrusted remote SVGs. Only the registry's own https origin is fetched, small plain documents are kept, anything active or external is refused, and a copy on disk is shown offline.
    internal static async Task Icons(string root)
    {
        Directory.CreateDirectory(root);
        const string registry = AcpRegistry.Url;
        const string own = "https://cdn.agentclientprotocol.com/registry/v1/latest/goose.svg";
        Require(AcpIconRules.IsAllowedUrl(own, registry) && AcpIconRules.IsAllowedUrl("https://cdn.agentclientprotocol.com/other/x.svg", registry), "An icon on the registry's own https host is allowed.");
        foreach (var url in new[] { "http://cdn.agentclientprotocol.com/x.svg", "https://evil.example/x.svg", "https://cdn.agentclientprotocol.com.evil.example/x.svg", "https://user:pw@cdn.agentclientprotocol.com/x.svg", "https://cdn.agentclientprotocol.com:8443/x.svg", "//cdn.agentclientprotocol.com/x.svg", "data:image/svg+xml,<svg/>", "goose.svg", "file:///etc/passwd" })
            Require(!AcpIconRules.IsAllowedUrl(url, registry), "The icon url " + url + " is refused.");
        Require(AcpIconRules.IsSafeId("goose") && AcpIconRules.IsSafeId("pi-acp") && !AcpIconRules.IsSafeId("../x") && !AcpIconRules.IsSafeId("a/b") && !AcpIconRules.IsSafeId("") && !AcpIconRules.IsSafeId(".hidden"), "An agent id becomes a file name only when it is plain.");

        Require(AcpIconRules.Reject(GoodIcon) is null, "A plain SVG with currentColor, a fixed colour and a local gradient is accepted.");
        var unsafeIcons = new Dictionary<string, string>
        {
            ["script element"] = """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""",
            ["foreignObject"] = """<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div/></foreignObject></svg>""",
            ["image"] = """<svg xmlns="http://www.w3.org/2000/svg"><image href="https://evil.example/a.png"/></svg>""",
            ["href"] = """<svg xmlns="http://www.w3.org/2000/svg"><use href="https://evil.example/a.svg#x"/></svg>""",
            ["xlink href"] = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><use xlink:href="data:image/svg+xml;base64,AAAA"/></svg>""",
            ["style url"] = """<svg xmlns="http://www.w3.org/2000/svg"><style>path{fill:url(https://evil.example/a.svg#x)}</style><path/></svg>""",
            ["style import"] = """<svg xmlns="http://www.w3.org/2000/svg"><style>@import 'https://evil.example/a.css';</style></svg>""",
            ["attribute url"] = """<svg xmlns="http://www.w3.org/2000/svg"><path style="fill:url( http://evil.example/a)"/></svg>""",
            ["event handler"] = """<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"/>""",
            ["doctype"] = """<!DOCTYPE svg [<!ENTITY x "y">]><svg xmlns="http://www.w3.org/2000/svg"/>""",
            ["not svg"] = """<html><body>captive portal</body></html>""",
            ["not xml"] = "<svg",
            ["too large"] = """<svg xmlns="http://www.w3.org/2000/svg"><!--""" + new string('x', AcpIconRules.MaxBytes) + """--></svg>"""
        };
        foreach (var (name, svg) in unsafeIcons) Require(AcpIconRules.Reject(svg) is not null, "The icon is refused: " + name + ".");

        var directory = Path.Combine(root, "icons");
        var calls = new List<(string Url, string? ETag)>();
        var online = true;
        var body = GoodIcon;
        var clock = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        AcpFetch fetch = (url, etag, modified, _) =>
        {
            calls.Add((url, etag));
            if (!online) throw new HttpRequestException("no network");
            return Task.FromResult(etag == "\"i1\"" ? new AcpFetchResult(304, null, etag, modified) : new AcpFetchResult(200, body, "\"i1\"", null));
        };
        var cache = new AcpIconCache(directory, fetch, () => clock);
        var wanted = new[] { ("goose", own), ("evil", "https://evil.example/e.svg"), ("../x", own) };
        var first = await cache.LoadAsync(wanted, false, false, CancellationToken.None);
        Require(first.Keys.Single() == "goose" && first["goose"] == GoodIcon && calls.Count == 1, "Only the icon on the registry's host is fetched, under a plain id.");
        Require((await cache.LoadAsync(wanted, false, true, CancellationToken.None)).ContainsKey("goose") && calls.Count == 1, "A cache-only read never touches the network.");
        clock = clock.AddHours(1);
        await cache.LoadAsync(wanted, false, false, CancellationToken.None);
        Require(calls.Count == 1, "A fresh icon is not fetched again.");
        clock = clock.AddDays(1);
        await cache.LoadAsync(wanted, false, false, CancellationToken.None);
        Require(calls.Count == 2 && calls[1].ETag == "\"i1\"", "A stale icon is revalidated with its ETag.");
        online = false;
        clock = clock.AddDays(3);
        var offline = await new AcpIconCache(directory, fetch, () => clock).LoadAsync(wanted, true, false, CancellationToken.None);
        Require(offline.ContainsKey("goose"), "Offline, the icon fetched before is still shown.");
        Require((await new AcpIconCache(Path.Combine(root, "icons-empty"), fetch, () => clock).LoadAsync(wanted, false, false, CancellationToken.None)).Count == 0, "Offline with no copy there is no icon, and the generic glyph stands in.");
        online = true;
        body = """<svg xmlns="http://www.w3.org/2000/svg"><script/></svg>""";
        clock = clock.AddDays(3);
        Require((await new AcpIconCache(directory, (u, _, m, c) => fetch(u, null, m, c), () => clock).LoadAsync(wanted, true, false, CancellationToken.None)).Count == 0 && !File.Exists(Path.Combine(directory, "goose.svg")), "An icon that turns unsafe is dropped with its old copy.");
        body = new string('x', AcpIconRules.MaxBytes + 1);
        Require((await new AcpIconCache(Path.Combine(root, "icons-big"), fetch, () => clock).LoadAsync(wanted, false, false, CancellationToken.None)).Count == 0, "An oversized answer is not kept.");

        Require(!Tereditor.Plugins.Acp.UI.AcpGlyph.HasIcon(null) && !Tereditor.Plugins.Acp.UI.AcpGlyph.HasIcon(" ") && Tereditor.Plugins.Acp.UI.AcpGlyph.HasIcon(GoodIcon), "Without an icon the generic glyph is drawn: custom agents, rejected, missing or not yet fetched icons.");
    }

    // Starting a chat for a registry agent fills that agent's icon once when the disk has none; custom agents and failures change nothing and never hold the chat up.
    internal static async Task ChatIcons(string root)
    {
        Directory.CreateDirectory(root);
        var fixture = AcpRegistry.Parse(FixtureText()) ?? throw new InvalidOperationException("acp: The captured registry does not parse.");
        var directory = Path.Combine(root, "icons");
        var calls = new List<string>();
        var logs = new List<string>();
        var online = true;
        var seenDuringFetch = new List<bool>();
        var gate = new TaskCompletionSource();
        var slow = false;
        AcpFetch fetch = async (url, _, _, _) =>
        {
            calls.Add(url);
            seenDuringFetch.Add(File.Exists(Path.Combine(directory, "goose.svg")));
            if (slow) await gate.Task;
            if (!online) throw new HttpRequestException("no network");
            return new AcpFetchResult(200, GoodIcon, "\"i1\"", null);
        };
        AcpAgentIcons make() => new(directory, fetch, _ => Task.FromResult<System.Text.Json.Nodes.JsonObject?>(fixture), logs.Add);
        var registryAgent = new AcpAgentEntry("goose", "Goose", "goose") { Origin = "registry" };

        var icons = make();
        Require(await icons.EnsureAsync(new AcpAgentEntry("mine", "Mine", "x")) is null && calls.Count == 0, "A custom agent fetches nothing.");
        Require(await icons.EnsureAsync(registryAgent) == GoodIcon && calls.Count == 1 && calls[0].EndsWith("goose.svg", StringComparison.Ordinal), "A chat for a registry agent downloads its icon once, from the registry's url.");
        Require(seenDuringFetch.SequenceEqual([false]) && File.Exists(Path.Combine(directory, "goose.svg")) && File.Exists(Path.Combine(directory, "goose.json")) && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "The icon is written atomically: nothing is visible before the download ends and no temporary file is left.");
        await icons.EnsureAsync(registryAgent);
        Require(calls.Count == 1, "Starting more chats of the same agent in this run does not download again.");
        Require(await make().EnsureAsync(registryAgent) == GoodIcon && calls.Count == 1, "An icon already on disk is not downloaded, even by a new host run.");

        File.Delete(Path.Combine(directory, "goose.svg"));
        online = false;
        var failing = make();
        Require(await failing.EnsureAsync(registryAgent) is null, "A failed download gives no icon.");
        var attempts = calls.Count;
        await failing.EnsureAsync(registryAgent);
        Require(calls.Count == attempts, "A failed download is not retried in the same run.");

        online = true;
        slow = true;
        var pending = make().EnsureAsync(registryAgent);
        Require(!pending.IsCompleted, "The download is still running; the caller got its task back at once, so a chat start that does not await it is never delayed.");
        gate.SetResult();
        Require(await pending == GoodIcon, "The icon arrives after the slow download.");

        var broken = new AcpAgentIcons(directory, fetch, _ => throw new InvalidOperationException("secret-token"), logs.Add);
        Require(await broken.EnsureAsync(new AcpAgentEntry("pi-acp", "Pi", "pi") { Origin = "registry" }) is null && logs.Count == 1 && !logs[0].Contains("secret-token", StringComparison.Ordinal), "An unexpected failure is logged without its message and does not escape.");

        var tabs = new Tereditor.Plugins.Acp.UI.AcpTabIcons();
        tabs.SetAgent("w", "c1", "goose");
        Require(tabs.AddIcons(new Dictionary<string, string> { ["goose"] = GoodIcon }) && tabs.SvgFor("w", "c1") == GoodIcon && !tabs.AddIcons(new Dictionary<string, string> { ["goose"] = GoodIcon }), "An icon fetched after the tab opened changes that tab once and keeps the other icons.");
    }

    // A chat tab wears the logo of its own agent from the icons already on disk; anything else keeps the generic ACP icon. No fetch is involved.
    internal static void TabIcons()
    {
        var goose = GoodIcon;
        var asked = new List<string>();
        var icons = new Tereditor.Plugins.Acp.UI.AcpTabIcons { AskAgents = asked.Add };
        static TabRef chat(string id) => new PluginDocumentTabRef("w", AcpContract.DocumentScheme, id);
        Require(icons.Decorate(chat("c1")) is null && asked.SequenceEqual(["w"]), "A restored tab with no known agent keeps the generic icon and asks the host once.");
        icons.Decorate(chat("c2"));
        Require(asked.Count == 1, "The host is asked once per workspace.");
        Require(!icons.SetAgents("w", new Dictionary<string, string> { ["c1"] = "goose", ["c2"] = "mine" }), "Agents without icons on disk change nothing on screen.");
        Require(icons.SetIcons(new Dictionary<string, string> { ["goose"] = goose }), "Icons arriving for a chat's agent change its tab.");
        Require(icons.Decorate(chat("c1")) is { IconSvg: { } svg, Icon: null } && svg == goose, "A chat tab of a known agent carries that agent's SVG, drawn through currentColor in the tab's text colour.");
        Require(icons.Decorate(chat("c2")) is null, "A custom agent, or one with no icon on disk, keeps the generic ACP icon.");
        Require(icons.Decorate(chat("unknown")) is null && icons.Decorate(new PluginDocumentTabRef("w", "other-scheme", "c1")) is null && icons.Decorate(new TerminalTabRef("w", "c1")) is null, "Other chats and other tabs are left alone.");
        Require(icons.SetIcons(new Dictionary<string, string> { ["goose"] = " " }) && icons.Decorate(chat("c1")) is null, "A blank icon is the generic one, never an empty slot.");
        Require(icons.SetAgent("w", "c3", "goose") is false, "A new chat of an agent without a usable icon changes nothing.");
        icons.SetIcons(new Dictionary<string, string> { ["goose"] = goose });
        Require(icons.SetAgent("w", "c1", "other") is false && icons.Decorate(chat("c1")) is null, "A chat moved to another agent follows that agent's icon.");
    }

    // The install confirmation shows the same facts as before in the overlay dialog: name and version, source, command, checksum, license, repository, the plain explanation and the not-recommended line.
    internal static void Confirmation()
    {
        var fixture = AcpRegistry.Parse(FixtureText())!;
        var goose = AcpRegistry.Plan(fixture, "goose", null, "darwin-aarch64")!;
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Title(goose) == "Install goose 1.57.0?", "The dialog is titled with the agent and version.");
        var facts = Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Facts(goose);
        Require(facts.Select(fact => fact.Label).SequenceEqual(["Source", "Runs", "Checked", "SHA-256", "License", "Repository"]) && facts[1].Value == "./goose acp" && facts[2].Value == AcpRegistry.Sha256Verified, "A binary install lists source, command, checksum, license and repository.");
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Facts(AcpRegistry.Plan(fixture, "pi-acp", null, "darwin-aarch64")!).Select(fact => fact.Label).SequenceEqual(["Source", "Runs", "Checked", "License", "Repository"]), "Facts the registry does not give are left out.");
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Explanation(goose).Contains("SHA-256", StringComparison.Ordinal) && !Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Explanation(goose).Contains(AcpInstallPlan.NotRecommendedLine, StringComparison.Ordinal), "The explanation says what is verified, with no warning for a normal entry.");
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Explanation(AcpRegistry.Plan(fixture, "pi-acp", null, "darwin-aarch64")!).EndsWith(AcpInstallPlan.NotRecommendedLine, StringComparison.Ordinal), "A not-recommended agent adds the warning line.");
    }

    // The launcher: a left click starts the default agent, the right-click menu lists every agent with the default marked.
    internal static void Launcher()
    {
        var one = new AcpAgentEntry("a", "A", "a");
        var two = new AcpAgentEntry("b", "B", "b");
        var three = new AcpAgentEntry("c", "C", "c");
        var launcher = typeof(Tereditor.Plugins.Acp.UI.AcpLauncher);
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("b", [one, two, three]) is { Kind: "start", Agent.Id: "b" }, "A default that is configured starts at once, without the picker.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("", [one, two, three]) is { Kind: "ask", Agent: null }, "With no default and several agents the picker opens.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("", [one]) is { Kind: "start", Agent.Id: "a" }, "With no default the only agent starts.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("gone", [one, two]) is { Kind: "ask" } && Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("gone", [one]) is { Kind: "start", Agent.Id: "a" }, "A removed default falls back to today's behaviour.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("a", []) is { Kind: "none" }, "With no agent at all there is nothing to start.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.DefaultIsMissing("gone", [one]) && !Tereditor.Plugins.Acp.UI.AcpLauncher.DefaultIsMissing("a", [one]) && !Tereditor.Plugins.Acp.UI.AcpLauncher.DefaultIsMissing("", [one]), "Only a set default that no agent has counts as removed.");
        var menu = Tereditor.Plugins.Acp.UI.AcpLauncher.MenuEntries("b", [one, two, three]);
        Require(menu.Select(entry => entry.Agent.Id).SequenceEqual(["a", "b", "c"]) && menu.Where(entry => entry.IsDefault).Select(entry => entry.Agent.Id).SequenceEqual(["b"]), "The right-click menu lists every agent in order with the default marked.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.MenuEntries("gone", [one, two]).All(entry => !entry.IsDefault) && new Tereditor.Plugins.Acp.AcpSettings().DefaultAgent == "", "A removed default marks nothing, and no default is the empty string.");
        Require(launcher.IsSealed || launcher.IsAbstract, "The launcher decisions are static.");
    }

    private static string Dump(AcpSignInInfo info) =>
        string.Join('\n', info.Lines.Select(line => line.Kind + "|" + line.Display()).Concat([info.Why, info.AgentId]).Concat(info.Methods.Select(method => $"{method.Id}|{method.Name}|{method.Description}|{method.Label}|{method.CommandLine}")));

    private static void FakeGh(string directory, string body)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "gh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // Where an agent's sign-in can come from, found without reading a secret: names, existence of files, the GitHub CLI account and the agent's own answer.
    internal static async Task SignIn(string root)
    {
        const string Secret = "ghp_SENTINEL_do_not_leak_1234";
        var windows = OperatingSystem.IsWindows();
        var home = Path.Combine(root, "home");
        var logged = Path.Combine(root, "bin-logged");
        var stderrOnly = Path.Combine(root, "bin-old");
        var loggedOut = Path.Combine(root, "bin-out");
        var argsLog = Path.Combine(root, "gh-args.log");
        Directory.CreateDirectory(home);
        if (!windows)
        {
            // The fake gh records its arguments, prints the account line like gh does, and a token line that must never be read or shown.
            FakeGh(logged, $"echo \"$@\" >> '{argsLog}'\necho 'github.com'\necho '  ✓ Logged in to github.com account octocat (keyring)'\necho '  - Token: {Secret}'\n");
            FakeGh(stderrOnly, $"echo \"$@\" >> '{argsLog}'\necho 'github.com' >&2\necho '  ✓ Logged in to github.com as olduser (keyring)' >&2\necho '  - Token: {Secret}' >&2\n");
            FakeGh(loggedOut, $"echo \"$@\" >> '{argsLog}'\necho 'You are not logged into any GitHub hosts. To log in, run: gh auth login' >&2\nexit 1\n");
        }
        Func<string?, Func<string, string?>> shell = path => name => name switch { "GH_TOKEN" => Secret, "PATH" => path, _ => null };
        Func<string?, Func<string, string?>> clean = path => name => name == "PATH" ? path : null;
        var copilot = new AcpAgentEntry("github-copilot-cli", "GitHub Copilot", "copilot") { Origin = "registry" };
        var ct = CancellationToken.None;
        var secrets = new List<string>();

        // The shell's GH_TOKEN comes first; before the agent has answered, the keychain item is not even mentioned, and the reason says so.
        var info = await new AcpSignIn(shell(logged), home).ResolveAsync(copilot, null, false, ct);
        secrets.Add(Dump(info));
        Require(info.Lines[0] is { Kind: "env", Text: "GH_TOKEN in your shell", CheckedFirst: true } && info.Lines[0].Display() == "GH_TOKEN in your shell (checked first)", "A present variable shows by name, marked as checked first.");
        Require(info.Why.Contains("COPILOT_GITHUB_TOKEN, GITHUB_TOKEN are not set", StringComparison.Ordinal) && info.Why.Contains("not looked up", StringComparison.Ordinal) && info.Why.Contains("checks them in this order: COPILOT_GITHUB_TOKEN, GH_TOKEN, GITHUB_TOKEN, stored login, GitHub CLI", StringComparison.Ordinal) && info.Why.Contains("has not told us which one it used", StringComparison.Ordinal), "The reason lists what is not set, why the keychain is skipped and the agent's order, and does not claim a winner.");
        Require(!info.Lines.Any(line => line.Kind == "stored"), "No stored-login line before the agent has run.");
        if (!windows)
        {
            Require(info.Lines.Select(line => line.Display()).SequenceEqual(["GH_TOKEN in your shell (checked first)", "GitHub CLI account octocat"]), "The GitHub CLI account follows the variable, unmarked.");
            Require(info.Why.Contains("GitHub CLI account octocat is used by Copilot as the last fallback", StringComparison.Ordinal), "The reason says the CLI account is the last fallback.");
        }

        // A variable set in the agent's own settings is read before the shell's, and the order is the agent's.
        var own = copilot with { Env = new Dictionary<string, string> { ["COPILOT_GITHUB_TOKEN"] = Secret } };
        info = await new AcpSignIn(shell(logged), home).ResolveAsync(own, null, false, ct);
        secrets.Add(Dump(info));
        Require(info.Lines.Take(2).Select(line => line.Display()).SequenceEqual(["COPILOT_GITHUB_TOKEN in this agent's settings (checked first)", "GH_TOKEN in your shell"]), "The agent's own variable comes first in the order and is the one marked.");

        // Once the agent has answered, the stored login shows as maybe there; the line that may win is not marked after it.
        var (started, fake) = await Started(AcpOffer.Copilot);
        var answer = new AcpAnswer(started.Agent!.AuthMethods, []);
        await started.DisposeAsync();
        info = await new AcpSignIn(clean(logged), home).ResolveAsync(copilot, answer, false, ct);
        secrets.Add(Dump(info));
        Require(info.Lines[0] is { Kind: "stored", CheckedFirst: false } && info.Lines[0].Text == "A stored login, if you ran `copilot login`", "After the agent answered, the stored login shows as something that may be there, not as found.");
        Require(info.Lines.All(line => !line.CheckedFirst), "Nothing is marked when an earlier source may be the one in use.");
        Require(info.Lines[^1] is { Kind: "agent" } last && last.Text == "A sign-in the agent will ask for: Log in with Copilot CLI (Copilot Login)", "The agent's own sign-in is listed with its terminal label.");
        Require(info.Methods.Single() is { Id: "copilot-login", Label: "Copilot Login", CommandLine: "/path/copilot login" } && info.Terminal is not null, "Copilot's method has no type but carries a terminal login; its command line is the exact one.");
        info = await new AcpSignIn(clean(logged), home).ResolveAsync(copilot, null, true, ct);
        Require(info.Lines[0].Kind == "stored", "A failed start also makes the stored login show.");

        // The GitHub CLI: status on stdout or on stderr, a logged-out answer, and no gh at all. The account name is all that is read.
        if (!windows)
        {
            info = await new AcpSignIn(clean(stderrOnly), home).ResolveAsync(copilot, null, false, ct);
            secrets.Add(Dump(info));
            Require(info.Lines.Single() is { Kind: "gh", Text: "GitHub CLI account olduser", CheckedFirst: true }, "An older gh that prints on stderr is read the same way, and the CLI is first when nothing else is there.");
            info = await new AcpSignIn(clean(loggedOut), home).ResolveAsync(copilot, null, false, ct);
            Require(info.Lines.Single().Kind == "none" && info.Why.Contains("no GitHub CLI login was found", StringComparison.Ordinal), "A logged-out gh adds no line and says so.");
        }
        info = await new AcpSignIn(clean(Path.Combine(root, "nothing")), home).ResolveAsync(copilot, null, false, ct);
        Require(info.Lines.Single().Kind == "none" && info.Why.Contains("(gh) was not found", StringComparison.Ordinal), "Without gh on the path the reason says it was not found.");

        // Your own model provider needs no login; only the variable's name is shown.
        info = await new AcpSignIn(name => name switch { "COPILOT_PROVIDER_BASE_URL" => "http://provider.example/secret-path", "PATH" => Path.Combine(root, "nothing"), _ => null }, home).ResolveAsync(copilot, null, false, ct);
        secrets.Add(Dump(info));
        Require(info.Lines[0] is { Kind: "provider", Text: "Your own model provider (COPILOT_PROVIDER_BASE_URL): no account login is needed" } && !Dump(info).Contains("secret-path", StringComparison.Ordinal), "A provider variable shows by name and no GitHub login is asked.");

        // An unknown agent: only credential-like variables of its own entry, no order claimed, and the GitHub CLI is not asked.
        var asked = File.Exists(argsLog) ? File.ReadAllLines(argsLog).Length : 0;
        var custom = new AcpAgentEntry("custom-x", "X", "/bin/x") { Env = new Dictionary<string, string> { ["X_API_KEY"] = Secret, ["PLAIN"] = "1" } };
        info = await new AcpSignIn(shell(logged), home).ResolveAsync(custom, null, false, ct);
        secrets.Add(Dump(info));
        Require(info.Lines.Single() is { Kind: "env", Text: "X_API_KEY in this agent's settings", CheckedFirst: false } && (File.Exists(argsLog) ? File.ReadAllLines(argsLog).Length : 0) == asked, "An unknown agent shows only its own credential-like variables, unmarked, and does not start gh.");

        // A credential file: only whether it exists is read, never what is in it.
        var table = new Dictionary<string, AcpCredentialFacts> { ["fake"] = new("Fake") { EnvNames = ["FAKE_TOKEN"], CredentialFiles = [".fake/auth.json"], KnownOrder = true } };
        var fakeEntry = new AcpAgentEntry("fake", "Fake", "/bin/fake");
        info = await new AcpSignIn(clean(null), home, table).ResolveAsync(fakeEntry, null, false, ct);
        Require(info.Lines.Single().Kind == "none" && info.Why.Contains("no stored login file at ~/.fake/auth.json", StringComparison.Ordinal), "A missing credential file is said in the reason.");
        Directory.CreateDirectory(Path.Combine(home, ".fake"));
        File.WriteAllText(Path.Combine(home, ".fake", "auth.json"), "{\"token\":\"" + Secret + "\"}");
        info = await new AcpSignIn(clean(null), home, table).ResolveAsync(fakeEntry, null, false, ct);
        secrets.Add(Dump(info));
        Require(info.Lines.Single() is { Kind: "stored", Text: "A stored login file (~/.fake/auth.json)", CheckedFirst: true }, "An existing credential file shows by path and is checked first.");

        // authMethods: with a type, the agent program runs again with the method's arguments and variable; an agent-type method has no terminal line.
        var typed = new AcpFakeAgent { TerminalType = true, AuthStatus = true, AuthRequired = true };
        var captured = new List<string>();
        var noticeEntry = new AcpAgentEntry("custom-typed", "Typed", "/bin/fake-agent") { Args = ["acp"] };
        AcpAnswer? seen = null;
        var detector = new AcpSignIn(clean(null), home);
        await using var failing = new AcpChatSession("ws", "chat-auth", "/work", () => Task.FromResult<IAcpTransport>(new FakeTransport(typed)), _ => { }, captured.Add, null,
            got => seen = got, () => detector.ResolveAsync(noticeEntry, seen, false, ct));
        failing.Begin();
        await failing.StartAsync(null);
        var expectedCommand = windows ? "/bin/fake-agent acp login --web" : "FAKE_LOGIN_MODE='it'\\''s web' /bin/fake-agent acp login --web";
        Require(failing.Header.Status == ChatStatus.Failed && typed.Methods.Contains("auth/status"), "A not signed-in agent fails the start, and auth/status is asked because the agent advertised it.");
        var notice = Items(failing).OfType<ChatNoticeItem>().Single(item => item.Category == ChatNoticeKinds.Failure);
        Require(notice.Action is { Label: "Run in terminal" } action && (windows || action.Command == expectedCommand), "The failure card carries the exact terminal command as its button.");
        Require(notice.Text.Contains("Signs in with:", StringComparison.Ordinal) && notice.Text.Contains("- A sign-in the agent will ask for: Sign in in a terminal", StringComparison.Ordinal) && notice.Text.Contains("- The agent says it uses: octocat", StringComparison.Ordinal) && notice.Text.Contains("- The agent says it uses: second-account", StringComparison.Ordinal) && notice.Text.Contains("- A sign-in the agent will ask for: Let the agent do it", StringComparison.Ordinal) && notice.Text.Contains("Why:", StringComparison.Ordinal), "The failure card lists where the sign-in comes from, with the agent's own methods and accounts.");
        info = await detector.ResolveAsync(noticeEntry, seen, false, ct);
        Require(info.Methods.Count == 2 && info.Methods[1].CommandLine is null && info.Terminal!.Id == "term", "Only the terminal-type method has a command line.");
        secrets.Add(notice.Text + "\n" + notice.Action?.Command + "\n" + string.Join('\n', captured));

        // An agent that does not advertise auth/status is never asked it.
        Require(!fake.Methods.Contains("auth/status"), "Copilot's initialize does not advertise auth/status, so it is not asked.");

        if (!windows)
            Require(File.ReadAllLines(argsLog).Length > 0 && File.ReadAllLines(argsLog).All(argsLine => argsLine == "auth status --hostname github.com"), "gh was run only as auth status for github.com: never auth token, and no keychain tool at all.");
        Require(secrets.All(text => !text.Contains(Secret, StringComparison.Ordinal)), "No token value shows in a line, a reason, a method, the failure card, its command or the log.");
    }

    internal static async Task Run(string root)
    {
        Manifest();
        Rows();
        MasterList();
        CombinedList();
        AgentList();
        CustomForm();
        Confirmation();
        Launcher();
        EntryText();
        Logo();
        await DefaultMode();
        await Protocol();
        await Session();
        await Pickers();
        await Approvals();
        Directory.CreateDirectory(root);
        await ClientMethods(root);
        await Registry(Path.Combine(root, "registry"));
        await Icons(Path.Combine(root, "icons"));
        TabIcons();
        await ChatIcons(Path.Combine(root, "chat-icons"));
        await Verification(Path.Combine(root, "verify"));
        await SignIn(Path.Combine(root, "signin"));
    }
}