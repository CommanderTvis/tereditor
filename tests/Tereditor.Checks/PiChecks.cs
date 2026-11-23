using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Chat;
using Tereditor.Plugins.Pi;
using Tereditor.Plugins.Pi.Host;
using Tereditor.Plugins.Pi.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks;

// The Pi plugin: its manifest (off by default, unavailable on a pure web host), the folding of Pi's RPC events and stored
// messages into chat items, and a session driven end to end against a fixture `pi` that speaks the RPC protocol.
internal static class PiChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("pi: " + message);
    }

    // The Settings tab's packages block at a 260 px pane: the install hint is whole and wraps, and the block keeps the
    // Settings spacing (16 between groups, 4 inside one) that Claude Code's Settings tab uses.
    internal static void PackagesLayout()
    {
        const string hintText = "Installs with pi install. Without Project settings, the package goes to USER settings. Restart the chat after a change.";
        var config = new PiConfigPanel(null!, "workspace");
        var snapshot = new PiConfigSnapshot("/work", "/agent", "trusted", "/agent/trust.json",
            [new PiConfigItem(PiConfigKinds.Package, PiScopes.User, "npm:@user/pkg", "/agent/settings.json", true, "")],
            [new PiConfigSetting("theme", PiScopes.User, "dark", true)], [], null, null, false);
        var content = config.SettingsSurface(snapshot);
        var window = new Window { Width = 260, Height = 800, Content = new ScrollViewer { Content = content } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var hint = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PiPackageHint");
        var block = window.GetVisualDescendants().OfType<StackPanel>().Single(stack => stack.Name == "PiPackagesBlock");
        var list = (StackPanel)block.Parent!;
        Require(hint.Text == hintText && hint.TextTrimming == TextTrimming.None && hint.TextWrapping == TextWrapping.Wrap,
            "The install hint keeps its whole text: no ellipsis, and it wraps.");
        Require(hint.Bounds.Width <= 260, "The install hint fits the 260 px pane.");
        var wide = Ui.Text(hintText, Ui.Hint, 12);
        wide.TextWrapping = TextWrapping.Wrap;
        wide.TextTrimming = TextTrimming.None;
        var probe = new Window { Width = 2000, Height = 200, Content = wide };
        probe.Show();
        Dispatcher.UIThread.RunJobs();
        probe.UpdateLayout();
        var oneLine = wide.Bounds.Height;
        Require(hint.Bounds.Height > oneLine * 1.5, "At 260 px the install hint wraps to several lines.");
        probe.Close();
        Require(list.Spacing == 16 && block.Spacing == 4, "Settings groups are 16 apart and sections 4 apart, as in Claude Code's Settings tab.");
        window.Close();
    }

    private static JsonElement Line(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal static void Manifest()
    {
        var manifest = PiManifest.Manifest;
        Require(manifest.Id == "pi" && !manifest.EnabledByDefault, "Pi is a builtin that is off by default.");
        Require(manifest.UnavailableOn.Count == 0, "Pi runs in a browser tab too, through PiLinks.InTab, so no host kind refuses it.");
        Require(PiContract.Contract.Id == manifest.Id, "The contract is the plugin's own.");
        Require(ChatContract.Methods.All(method => PiContract.Contract.Methods.Contains(method)), "Pi serves every chat method.");
    }

    internal static void Fold()
    {
        var folder = new PiFolder();
        string[] events =
        [
            """{"type":"agent_start"}""",
            """{"type":"message_start","message":{"role":"user","content":"list files"}}""",
            """{"type":"message_start","message":{"role":"assistant","content":[]}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_start","contentIndex":0}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_delta","contentIndex":0,"delta":"Let me look."}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_end","contentIndex":0,"content":"Let me look."}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"text_start","contentIndex":1}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":1,"delta":"Running "}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":1,"delta":"ls."}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"toolcall_end","contentIndex":2,"toolCall":{"id":"call1","name":"bash","arguments":{"command":"ls -la"}}}}""",
            """{"type":"tool_execution_start","toolCallId":"call1","toolName":"bash","args":{"command":"ls -la"}}""",
            """{"type":"tool_execution_update","toolCallId":"call1","toolName":"bash","partialResult":{"content":[{"type":"text","text":"total 1"}]}}""",
            """{"type":"tool_execution_end","toolCallId":"call1","toolName":"bash","result":{"content":[{"type":"text","text":"total 1\nfile.txt"}]},"isError":false}""",
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Let me look."},{"type":"text","text":"Running ls."},{"type":"toolCall","id":"call1","name":"bash","arguments":{"command":"ls -la"}}],"usage":{"input":100,"output":20,"cacheRead":0,"cacheWrite":0,"cost":{"total":0.002}},"stopReason":"toolUse"}}""",
            """{"type":"agent_end","messages":[]}"""
        ];
        foreach (var json in events) Require(folder.Apply(Line(json)), "A documented event is folded: " + json[..Math.Min(60, json.Length)]);
        Require(folder.Items.Select(item => item.GetType()).SequenceEqual([typeof(ChatUserItem), typeof(ChatThinkingItem), typeof(ChatAssistantItem), typeof(ChatToolItem)]),
            "A turn folds to the prompt, the thinking, the reply and the tool call, in order.");
        Require(folder.Items.OfType<ChatAssistantItem>().Single() is { Text: "Running ls.", Streaming: false }, "Streamed deltas meet in one finished reply.");
        var tool = folder.Items.OfType<ChatToolItem>().Single();
        Require(tool is { Name: "bash", State: ChatToolState.Done, Summary: "ls -la" } && tool.Output == "total 1\nfile.txt", "The tool card carries its command, state and final output.");
        Require(folder.Header is { Status: ChatStatus.Idle } && folder.Header.Usage is { Input: 100, Output: 20 } && Math.Abs(folder.Header.Usage.CostUsd - 0.002) < 1e-9, "The turn's usage lands in the header and the chat is idle again.");

        var failing = new PiFolder();
        failing.Apply(Line("""{"type":"tool_execution_start","toolCallId":"c2","toolName":"edit","args":{"path":"a.cs"}}"""));
        failing.Apply(Line("""{"type":"tool_execution_end","toolCallId":"c2","toolName":"edit","result":{"content":[{"type":"text","text":"no match"}]},"isError":true}"""));
        Require(failing.Items.OfType<ChatToolItem>().Single() is { State: ChatToolState.Failed, FilePath: "a.cs" }, "A failed edit is marked failed and keeps its file for the diff.");

        var queued = new PiFolder();
        var waiting = queued.AddQueued("also tests");
        queued.Apply(Line("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"also tests"}]}}"""));
        Require(queued.Items.OfType<ChatUserItem>().Single() is { Queued: false } released && released.Id == waiting.Id, "Pi's echo of a queued message releases that row instead of adding another.");

        var replay = new PiFolder();
        replay.Replay([
            Line("""{"role":"user","content":"hi"}"""),
            Line("""{"role":"assistant","content":[{"type":"text","text":"hello"},{"type":"toolCall","id":"t1","name":"read","arguments":{"path":"a.txt"}}],"usage":{"input":1,"output":1}}"""),
            Line("""{"role":"toolResult","toolCallId":"t1","toolName":"read","content":[{"type":"text","text":"contents"}],"isError":false}""")]);
        Require(replay.Items.Select(item => item.GetType()).SequenceEqual([typeof(ChatUserItem), typeof(ChatAssistantItem), typeof(ChatToolItem)])
            && replay.Items.OfType<ChatToolItem>().Single() is { State: ChatToolState.Done, Output: "contents" }, "A stored session replays as the same items a live one folds to.");

        var long_ = new PiFolder();
        var big = new string('x', ChatContract.OutputLimit + 10);
        long_.Apply(Line("{\"type\":\"tool_execution_start\",\"toolCallId\":\"c3\",\"toolName\":\"bash\",\"args\":{\"command\":\"cat big\"}}"));
        long_.Apply(Line("{\"type\":\"tool_execution_end\",\"toolCallId\":\"c3\",\"toolName\":\"bash\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"" + big + "\"}]},\"isError\":false}"));
        Require(long_.Items.OfType<ChatToolItem>().Single() is { OutputTruncated: true } cut && cut.Output!.Length == ChatContract.OutputLimit && long_.Detail("c3") == big, "Output over the wire limit is cut and served whole as the item's detail.");
    }

    // A fixture pi: answers the RPC commands the session sends and plays one short turn for a prompt.
    private static string FakePi(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pi");
        File.WriteAllText(path, """
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "0.99.1"; exit 0; fi
            printf '%s\n' "$@" > "$(dirname "$0")/argv.txt"
            printf '%s' "$PI_CODING_AGENT_DIR" > "$(dirname "$0")/agentdir.txt"
            while IFS= read -r line; do
              id=$(printf '%s' "$line" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
              case "$line" in
                *'"type":"get_state"'*) printf '{"id":"%s","type":"response","command":"get_state","success":true,"data":{"model":{"provider":"fake","id":"m1","contextWindow":1000},"thinkingLevel":"medium","sessionFile":"/nonexistent/s.jsonl","sessionId":"s1"}}\n' "$id" ;;
                *'"type":"get_available_models"'*) printf '{"id":"%s","type":"response","command":"get_available_models","success":true,"data":{"models":[{"provider":"fake","id":"m1","name":"Model One","reasoning":true},{"provider":"fake","id":"m2","name":"Model Two","reasoning":false}]}}\n' "$id" ;;
                *'"type":"get_commands"'*) printf '{"id":"%s","type":"response","command":"get_commands","success":true,"data":{"commands":[{"name":"skill:x","description":"A skill","source":"skill"}]}}\n' "$id" ;;
                *'"type":"set_model"'*) printf '{"id":"%s","type":"response","command":"set_model","success":true,"data":{}}\n' "$id" ;;
                *'"type":"set_thinking_level"'*) printf '{"id":"%s","type":"response","command":"set_thinking_level","success":true}\n' "$id" ;;
                *'"type":"prompt"'*)
                  printf '{"id":"%s","type":"response","command":"prompt","success":true}\n' "$id"
                  printf '{"type":"agent_start"}\n'
                  printf '{"type":"message_start","message":{"role":"user","content":"hello pi"}}\n'
                  printf '{"type":"message_start","message":{"role":"assistant","content":[]}}\n'
                  printf '{"type":"message_update","assistantMessageEvent":{"type":"text_start","contentIndex":0}}\n'
                  printf '{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"Hi there."}}\n'
                  printf '{"type":"message_update","assistantMessageEvent":{"type":"text_end","contentIndex":0,"content":"Hi there."}}\n'
                  printf '{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Hi there."}],"usage":{"input":5,"output":3},"stopReason":"stop"}}\n'
                  printf '{"type":"agent_end","messages":[]}\n' ;;
                *'"type":"abort"'*) printf '{"id":"%s","type":"response","command":"abort","success":true}\n' "$id" ;;
              esac
            done
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static async Task Session(string root)
    {
        var directory = Path.Combine(root, "pi");
        var fake = FakePi(directory);
        var pushes = new List<ChatPush>();
        var remembered = new List<string?>();
        await using var session = new PiChatSession("ws", "chat1", push => { lock (pushes) pushes.Add(push); }, _ => { }, (_, file) => remembered.Add(file));
        var spawn = new PiSpawn(fake, directory) { Model = "fake/m1", Thinking = "high", Codemode = true, Environment = new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = Path.Combine(directory, "profile") } };
        Require(session.Begin(spawn) is { Status: ChatStatus.Starting, Cwd: { } cwd } && cwd == directory, "Begin reports a starting chat in its directory.");
        await session.StartAsync(spawn);
        await Until(() => session.Header.Status == ChatStatus.Idle, "The session starts, reads Pi's state and goes idle.");
        var header = session.Header;
        Require(header.Model == "fake/m1" && header.Effort == "high" && header.SessionId == "s1", "The header carries the model, thinking level and session id Pi reported.");
        Require(header.Models.Select(model => model.Id).SequenceEqual(["fake/m1", "fake/m2"]) && header.Models[0].Efforts.Count > 0 && header.Models[1].Efforts.Count == 0, "Models come from Pi, with thinking levels only for reasoning ones.");
        Require(header.Commands.Any(command => command.Name == "skill:x"), "Pi's commands feed the slash menu.");
        Require(File.ReadAllText(Path.Combine(directory, "agentdir.txt")) == Path.Combine(directory, "profile"), "A model source's agent directory reaches the Pi child through its environment, never its arguments.");
        Require(File.ReadAllLines(Path.Combine(directory, "argv.txt")).Take(6).SequenceEqual(["--mode", "rpc", "--model", "fake/m1", "--tools", "+codemode"]), "Pi starts in RPC mode with the chosen model and codemode on.");

        await session.SendAsync("hello pi", null);
        await Until(() => session.Header.Status == ChatStatus.Idle && session.Snapshot().OfType<ChatItemPush>().Any(push => push.Item is ChatAssistantItem { Text: "Hi there." }), "A prompt streams a reply and the chat returns to idle.");
        var items = session.Snapshot().OfType<ChatItemPush>().Select(push => push.Item).ToArray();
        Require(items.Length == 2 && items[0] is ChatUserItem { Text: "hello pi" }, "The conversation is the prompt and the reply.");
        Require(session.Header.Usage is { Input: 5, Output: 3 }, "Usage is counted.");

        header = await session.SetModelAsync("fake/m2");
        Require(header.Model == "fake/m2", "A model change is confirmed by Pi before the header shows it.");
        header = await session.SetEffortAsync("low");
        Require(header.Effort == "low", "A thinking-level change is reflected.");
        await session.CloseAsync();
        Require(session.Header.Status == ChatStatus.Exited, "Closing ends the child.");

        var missing = new PiChatSession("ws", "chat2", _ => { }, _ => { }, (_, _) => { });
        var absent = new PiSpawn(Path.Combine(directory, "no-such-pi"), directory);
        missing.Begin(absent);
        await missing.StartAsync(absent);
        Require(missing.Header is { Status: ChatStatus.Failed, Error: { } error } && error.Contains("not found", StringComparison.Ordinal), "A missing pi fails the chat with an install hint.");

        // A launch contribution that replaces the executable (central run pi --) starts Pi in place of the command, and a blocked one starts nothing.
        var wrapped = new PiChatSession("ws", "chat3", _ => { }, _ => { }, (_, _) => { });
        var through = new PiSpawn(Path.Combine(directory, "no-such-pi"), directory) { Wrapper = [fake, "--wrapped"] };
        wrapped.Begin(through);
        await wrapped.StartAsync(through);
        await Until(() => wrapped.Header.Status == ChatStatus.Idle, "A wrapper replaces the executable and Pi starts.");
        Require(File.ReadAllLines(Path.Combine(directory, "argv.txt")).Take(3).SequenceEqual(["--wrapped", "--mode", "rpc"]), "The wrapper's words come before Pi's own arguments.");
        await wrapped.CloseAsync();
        var blockedSession = new PiChatSession("ws", "chat4", _ => { }, _ => { }, (_, _) => { });
        var blocked = new PiSpawn(fake, directory) { Blocked = "Central is not signed in." };
        blockedSession.Begin(blocked);
        await blockedSession.StartAsync(blocked);
        Require(blockedSession.Header is { Status: ChatStatus.Failed, Error: "Central is not signed in." }, "A blocked launch contribution stops Pi from starting, with its reason.");

        var status = await PiHost.ReadStatusAsync(fake);
        Require(status is { Installed: true, Version: "0.99.1" }, "The version probe reads pi --version.");
        Require(!(await PiHost.ReadStatusAsync(Path.Combine(directory, "none"))).Installed, "A missing pi is reported as not installed.");
    }

    // A fixture bun: `install` writes a package whose lock file names the pinned integrity (mode good), another one (bad) or waits (slow, hang);
    // `--bun <script> …` hands over to the fixture pi. It records its arguments, working directory and BUN_INSTALL.
    private static string FakeBun(string directory)
    {
        var path = Path.Combine(directory, "bun");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            here="$(dirname "$0")"
            printf '%s\n' "$*" >> "$here/bun-calls.txt"
            if [ "$1" = "install" ]; then
              printf '%s|%s\n' "$PWD" "$BUN_INSTALL" >> "$here/bun-install.txt"
              mode=$(cat "$here/bun-mode.txt")
              [ "$mode" = "hang" ] && sleep 30
              [ "$mode" = "slow" ] && sleep 1
              pkg=node_modules/@earendil-works/pi-coding-agent
              mkdir -p "$pkg/dist/bundle"
              printf '{"name":"x","version":"{{PiManaged.Version}}"}' > "$pkg/package.json"
              echo "// cli" > "$pkg/dist/bundle/cli.js"
              if [ "$mode" = "bad" ]; then echo '"{{PiManaged.Package}}": ["x", "sha512-other"]' > bun.lock; else echo '"{{PiManaged.Package}}": ["x", "{{PiManaged.Integrity}}"]' > bun.lock; fi
              exit 0
            fi
            if [ "$1" = "--bun" ]; then shift 2; exec "$here/pi" "$@"; fi
            exit 3
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    // The pinned Pi: downloaded once with Bun and checked against the pinned integrity, then run as `bun --bun cli.js`, never the system pi.
    internal static async Task Managed(string root)
    {
        var directory = Path.Combine(root, "pi-managed");
        Directory.CreateDirectory(directory);
        FakePi(directory);
        var bun = FakeBun(directory);
        var mode = (string value) => File.WriteAllText(Path.Combine(directory, "bun-mode.txt"), value);
        var calls = () => File.Exists(Path.Combine(directory, "bun-calls.txt")) ? File.ReadAllLines(Path.Combine(directory, "bun-calls.txt")) : Array.Empty<string>();
        var resolution = new RuntimeResolution(RuntimeKind.Bun, true, bun, ["x"], new Dictionary<string, string> { ["BUN_INSTALL"] = Path.Combine(directory, "bun-home") }, RuntimeSource.Downloaded, "", false);
        var install = Path.Combine(directory, "installs");
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);
        var managed = new PiManaged(install, (_, _) => Task.FromResult(resolution));
        var empty = () => !Directory.Exists(install) || Directory.GetFileSystemEntries(install).Length == 0;

        Require(managed.Status(PiSources.Managed) is { Installed: false, Source: PiSources.Managed, PinnedVersion: PiManaged.Version, ManagedVersion: "", Installing: false }, "Before a download the managed Pi is pinned and not installed.");
        Require(new PiSettings().Source == PiSources.System, "A fresh setup runs the system command.");
        Require(new PiSettings().Codemode, "A fresh Pi setting turns codemode on.");

        mode("bad");
        var refused = await Throws<InvalidOperationException>(() => managed.EnsureAsync(workspace, CancellationToken.None));
        Require(refused.Message.Contains("checksum", StringComparison.Ordinal) && managed.LastError.Contains("checksum", StringComparison.Ordinal), "A download whose lock file does not name the pinned integrity is refused with a reason.");
        Require(empty() && managed.InstalledVersion().Length == 0, "A refused download leaves nothing behind.");

        mode("hang");
        var hanging = managed.EnsureAsync(workspace, CancellationToken.None);
        await Until(() => managed.Running && managed.Status(PiSources.Managed) is { Installing: true, Progress.Length: > 0 }, "A running download reports that it runs, with a progress line.");
        managed.Cancel();
        await Throws<OperationCanceledException>(() => hanging);
        Require(empty() && managed.LastError.Contains("cancelled", StringComparison.Ordinal) && !managed.Running, "Cancel stops Bun, leaves no partial folder and says so.");

        // An older pin's copy is shown as such and replaced by the update.
        Directory.CreateDirectory(Path.Combine(install, "0.1.0"));
        Require(managed.Status(PiSources.Managed) is { Installed: false, ManagedVersion: "0.1.0" }, "A copy from an earlier pin is not the pinned Pi, so the page offers an update.");
        File.Delete(Path.Combine(directory, "bun-calls.txt"));
        mode("slow");
        await Task.WhenAll(managed.EnsureAsync(workspace, CancellationToken.None), managed.EnsureAsync(workspace, CancellationToken.None));
        Require(calls().Count(line => line.StartsWith("install", StringComparison.Ordinal)) == 1, "Two requests for the pinned Pi (the page and a chat) share one download.");
        Require(calls()[0] == "install --ignore-scripts --no-progress --registry=https://registry.npmjs.org/", "Bun installs without the package's scripts, from the npm registry.");
        var line = File.ReadAllLines(Path.Combine(directory, "bun-install.txt")).Last().Split('|');
        Require(Path.GetFullPath(line[0]).StartsWith(Path.GetFullPath(install), StringComparison.Ordinal) && line[1] == Path.Combine(directory, "bun-home"), "Bun installs in the download folder, never the workspace, with the host's Bun environment.");
        Require(managed.InstalledVersion() == PiManaged.Version && managed.Status(PiSources.Managed) is { Installed: true, ManagedVersion: PiManaged.Version, Version: PiManaged.Version }, "A checked download is the installed pinned Pi.");
        Require(!Directory.Exists(Path.Combine(install, "0.1.0")) && Directory.GetFileSystemEntries(install).Length == 1, "The update removes the earlier copy and leaves one folder.");
        Require(File.ReadAllText(Path.Combine(install, PiManaged.Version, "node_modules", "@earendil-works", "pi-coding-agent", "package.json")).Contains(PiManaged.Version, StringComparison.Ordinal), "The package is installed at the pinned version.");
        File.Delete(Path.Combine(directory, "bun-calls.txt"));
        await managed.EnsureAsync(workspace, CancellationToken.None);
        Require(calls().Length == 0, "An installed pinned Pi is not downloaded again.");

        // The chat runs the pinned copy through Bun and nothing else.
        var tracked = new List<int>();
        var spawn = await managed.PrepareAsync(new PiSpawn("pi", workspace) { Model = "fake/m1", Codemode = true, Environment = new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = Path.Combine(directory, "profile") } }, (runtime, id) => { Require(runtime == resolution, "The process is tracked against the Bun that ran it."); tracked.Add(id); });
        Require(spawn.Command == bun && spawn.Prefix.SequenceEqual(["--bun", managed.EntryPoint]) && spawn.Environment["BUN_INSTALL"] == Path.Combine(directory, "bun-home") && spawn.Environment["PI_CODING_AGENT_DIR"] == Path.Combine(directory, "profile"),
            "The managed launch is bun --bun cli.js with Bun's environment added to the model source's.");
        await using var session = new PiChatSession("ws", "managed", _ => { }, _ => { }, (_, _) => { });
        session.Begin(spawn);
        await session.StartAsync(spawn);
        await Until(() => session.Header.Status == ChatStatus.Idle, "The pinned Pi starts through Bun and the chat goes idle.");
        Require(calls().Contains($"--bun {managed.EntryPoint} --mode rpc --model fake/m1 --tools +codemode") && tracked.Count == 1, "Bun gets the script first and Pi's own arguments after it, and the process is tracked.");
        Require(File.ReadAllLines(Path.Combine(directory, "argv.txt")).Take(6).SequenceEqual(["--mode", "rpc", "--model", "fake/m1", "--tools", "+codemode"]), "Pi itself sees only its own arguments, codemode included.");
        await session.CloseAsync();

        var blocked = new PiSpawn("pi", workspace) { Blocked = "Central is not signed in." };
        Require(await managed.PrepareAsync(blocked, (_, _) => throw new InvalidOperationException("tracked")) == blocked, "A blocked launch is passed on unchanged, so it fails with its reason and starts no Bun.");

        var noBun = new PiManaged(install, (_, _) => Task.FromResult(RuntimeResolution.Unavailable(RuntimeKind.Bun, "Needs Bun.", offerEnable: true)));
        managed.Remove();
        Require(!Directory.Exists(install) && managed.InstalledVersion().Length == 0, "Remove deletes the downloaded Pi.");
        var missing = await Throws<InvalidOperationException>(() => noBun.PrepareAsync(new PiSpawn("pi", workspace), (_, _) => { }));
        Require(missing.Message == "Needs Bun." && noBun.LastError.Contains("Needs Bun.", StringComparison.Ordinal), "Without Bun the chat fails with Runtimes' own sentence and never falls back to the system Pi.");

        // The system source is unchanged.
        Require((await PiHost.ReadStatusAsync(new PiSettings { Command = FakePiPath(directory) }, managed)) is { Installed: true, Version: "0.99.1", Source: PiSources.System, PinnedVersion: PiManaged.Version }, "The system source still reads pi --version, and also reports the pin.");
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("pi: expected " + typeof(T).Name + ".");
    }

    private static string FakePiPath(string directory) => Path.Combine(directory, "pi");

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("pi: " + message);
            await Task.Delay(25);
        }
    }

    internal static void Sessions(string root)
    {
        var sessions = Path.Combine(root, "pi-sessions");
        var cwd = "/work/project";
        var directory = Path.Combine(sessions, "--work-project--");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.jsonl"), "{\"type\":\"session\",\"id\":\"a\"}\n{\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":\"fix the build\"}}\n");
        File.WriteAllText(Path.Combine(directory, "b.jsonl"), "{\"type\":\"session\",\"id\":\"b\"}\n{\"type\":\"session_info\",\"name\":\"Named\"}\n");
        File.SetLastWriteTimeUtc(Path.Combine(directory, "a.jsonl"), DateTime.UtcNow.AddMinutes(-5));
        var host = new PiChatHost(null!, () => sessions);
        var list = host.ListSessions(cwd).Sessions;
        Require(list.Select(session => session.Title).SequenceEqual(["Named", "fix the build"]), "Past sessions list newest first, titled by name or first prompt.");
        Require(host.ListSessions("/elsewhere").Sessions.Count == 0, "A directory with no sessions lists none.");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // The pane's reads against a fixture home and project: which files a session loads, the trust gate, the nearest trust decision and the scope labels.
    internal static void Config(string root)
    {
        var fixture = Path.Combine(root, "config");
        if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        var home = Path.Combine(fixture, "home");
        var agent = Path.Combine(home, ".pi", "agent");
        var repo = Path.Combine(fixture, "repo");
        var cwd = Path.Combine(repo, "sub");
        Write(Path.Combine(agent, "AGENTS.md"), "user");
        Write(Path.Combine(agent, "CLAUDE.md"), "not read: AGENTS.md wins in one directory");
        Write(Path.Combine(agent, "extensions", "ext1.ts"), "");
        Write(Path.Combine(agent, "skills", "s1", "SKILL.md"), "");
        Write(Path.Combine(agent, "prompts", "p1.md"), "");
        Write(Path.Combine(agent, "mcp.json"), """{"mcpServers":{"docs":{}}}""");
        Write(Path.Combine(agent, "settings.json"), """{"defaultProjectTrust":"ask","packages":["npm:@user/pkg"]}""");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Write(Path.Combine(repo, "AGENTS.override.md"), "repo");
        Write(Path.Combine(cwd, ".pi", "extensions", "pext.ts"), "");
        Write(Path.Combine(cwd, ".pi", "prompts", "pp.md"), "");
        Write(Path.Combine(cwd, ".pi", "mcp.json"), """{"mcpServers":{"proj":{}}}""");
        Write(Path.Combine(cwd, ".pi", "settings.json"), """{"packages":[{"source":"git:github.com/x/y"}],"theme":"dark"}""");
        Write(Path.Combine(repo, ".agents", "skills", "a1", "SKILL.md"), "");
        Directory.CreateDirectory(cwd);
        var trustFile = Path.Combine(agent, "trust.json");
        var roots = new PiConfigRoots(agent, home, cwd);

        PiConfigSnapshot Read(Dictionary<string, bool> decisions, bool chat = false)
        {
            File.WriteAllText(trustFile, System.Text.Json.JsonSerializer.Serialize(decisions));
            var commands = new[] { new PiConfigCommand("review", "prompt", "Review") };
            return PiConfigReader.Read(roots, chat ? commands : [], chat ? "fake/m1" : null, chat ? "high" : null, chat);
        }
        // Only what sits under this fixture, so a machine's own parent directories never change the answer.
        IEnumerable<PiConfigItem> Under(PiConfigSnapshot snapshot, string kind) => snapshot.Items.Where(item => item.Kind == kind && item.Path.StartsWith(fixture, StringComparison.Ordinal));
        string Names(IEnumerable<PiConfigItem> items) => string.Join(",", items.Select(item => item.Name + "/" + item.Scope + "/" + item.Loaded));

        var trusted = Read(new() { [repo] = true }, chat: true);
        Require(trusted.Trust == "trusted" && trusted.ChatOpen && trusted.Model == "fake/m1" && trusted.Thinking == "high" && trusted.Commands.Single().Name == "review",
            "A trusted project reads as trusted, and the open chat's model, thinking level and commands come with it.");
        Require(Names(Under(trusted, PiConfigKinds.Context)) == "AGENTS.md/user/True,AGENTS.override.md/project/True",
            "One context file per directory, the agent directory's first, and the project's override in its own directory.");
        Require(Names(Under(trusted, PiConfigKinds.Extension)) == "ext1.ts/user/True,pext.ts/project/True", "Extensions come from the agent directory and from .pi, when the project is trusted.");
        Require(Names(Under(trusted, PiConfigKinds.Skill)) == "s1/user/True,a1/project/True", "Skills come from the agent directory and from .agents/skills up to the repository root.");
        Require(Names(Under(trusted, PiConfigKinds.Prompt)) == "p1/user/True,pp/project/True", "Prompt templates come from the agent directory and from .pi/prompts.");
        Require(Names(Under(trusted, PiConfigKinds.Mcp)) == "docs/user/True,proj/project/True", "MCP servers come from both mcp.json files.");
        Require(Names(Under(trusted, PiConfigKinds.Package)) == "npm:@user/pkg/user/True,git:github.com/x/y/project/True", "Packages come from both settings files.");
        Require(trusted.Settings.Any(setting => setting.Key == "theme" && setting.Scope == PiScopes.Project && setting.Loaded), "A project setting is listed once the project is trusted.");

        var declined = Read(new() { [repo] = false });
        Require(declined.Trust == "not trusted", "A declined project reads as not trusted.");
        Require(Under(declined, PiConfigKinds.Extension).Single(item => item.Name == "pext.ts") is { Loaded: false, Detail: "Not loaded: this project is not trusted." },
            "A declined project's extension is listed as not loaded.");
        Require(Under(declined, PiConfigKinds.Mcp).Single(item => item.Path.Contains(Path.Combine(".pi", "mcp.json"), StringComparison.Ordinal)) is { Loaded: false, Name: "mcp.json" }, "A declined project's mcp.json is listed as not loaded, with none of its servers.");
        Require(Under(declined, PiConfigKinds.Context).Single(item => item.Name == "AGENTS.override.md") is { Loaded: true }, "Context files load regardless of trust.");
        Require(declined.Settings.Any(setting => setting.Key == "theme" && !setting.Loaded), "A declined project's setting is not loaded.");

        var undecided = Read(new());
        Require(undecided.Trust == "not decided" && Under(undecided, PiConfigKinds.Skill).Any(item => item.Name == "a1" && item.Detail.StartsWith("Not loaded until the project is trusted", StringComparison.Ordinal)),
            "With no decision, the project's resources wait for /trust.");

        // The closest decision wins: a parent's trust covers the project unless the project has its own.
        var inherited = Read(new() { [fixture] = true });
        Require(inherited.Trust == "trusted", "A parent directory's trust decision applies to the project below it.");
        var nearer = Read(new() { [fixture] = true, [repo] = false });
        Require(nearer.Trust == "not trusted", "The nearest decision wins over a parent's.");

        Require(PiConfigReader.ScopeLabel(PiScopes.Project) == "PROJECT" && PiConfigReader.ScopeLabel(PiScopes.User) == "USER", "Scope labels are USER and PROJECT; -l maps to PROJECT.");
        var contributed = new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = "/contributed" };
        Require(PiConfigReader.AgentDir(contributed, "/environment", home) == "/contributed"
            && PiConfigReader.AgentDir(new Dictionary<string, string>(), "/environment", home) == "/environment"
            && PiConfigReader.AgentDir(new Dictionary<string, string>(), null, home) == agent,
            "The agent directory follows the model source, then PI_CODING_AGENT_DIR, then ~/.pi/agent.");
        File.Delete(trustFile);
    }

    // Install and remove argument building, and one run of a fake pi that records its arguments and working directory.
    internal static async Task Packages(string root)
    {
        Require(PiPackages.ValidSource("npm:@s/p") && !PiPackages.ValidSource("-x") && !PiPackages.ValidSource("a b") && !PiPackages.ValidSource(""), "A package source is one argument and not a flag.");
        Require(PiPackages.Arguments("install", "npm:@s/p", false).SequenceEqual(["install", "npm:@s/p"])
            && PiPackages.Arguments("remove", "npm:@s/p", true).SequenceEqual(["remove", "npm:@s/p", "-l"]), "Install and remove take the source, and -l for the project.");
        var dir = Path.Combine(root, "packages");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "pi");
        File.WriteAllText(script, """
            #!/bin/sh
            printf '%s\n' "$@" > "$(dirname "$0")/argv.txt"
            pwd > "$(dirname "$0")/cwd.txt"
            if [ "$2" = "npm:fail" ]; then echo "no such package"; exit 3; fi
            echo "installed"
            """);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var done = await PiPackages.RunAsync(new PiSpawn(script, dir), PiPackages.Arguments("install", "npm:@s/p", true), CancellationToken.None);
        Require(done.Ok && done.Output == "installed", "A successful install reports its output.");
        Require(File.ReadAllText(Path.Combine(dir, "argv.txt")) == "install\nnpm:@s/p\n-l\n", "The install runs pi with the verb, the source and -l, and nothing else.");
        Require(Path.GetFileName(File.ReadAllText(Path.Combine(dir, "cwd.txt")).Trim()) == "packages", "Pi runs in the workspace directory.");
        var failed = await PiPackages.RunAsync(new PiSpawn(script, dir), PiPackages.Arguments("install", "npm:fail", false), CancellationToken.None);
        Require(!failed.Ok && failed.Output.Contains("no such package", StringComparison.Ordinal), "A failed install reports pi's message and is not ok.");
        var blocked = await PiPackages.RunAsync(new PiSpawn(script, dir) { Blocked = "Blocked by the model source." }, PiPackages.Arguments("remove", "npm:@s/p", false), CancellationToken.None);
        Require(!blocked.Ok && blocked.Output == "Blocked by the model source.", "A blocked launch runs nothing and says why.");
    }

    internal static async Task Run(string root)
    {
        Manifest();
        Fold();
        Sessions(root);
        await Session(root);
        await Managed(root);
        Config(root);
        await Packages(root);
    }
}