using System.Text.Json.Nodes;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Lsp.IntelliJ;

namespace Tereditor.Checks;

/// <summary>A fake licensing server (an in-process LSP peer) behind the IntelliJ plugin's license state and actions.</summary>
internal static class IntelliJLicenseChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>Answers <c>jetbrains/licensing/*</c> and pushes whole states before its replies, as the server does.</summary>
    private sealed class FakeServer : ILanguageServerSession
    {
        internal JsonObject State = new() { ["featuresDeclined"] = true, ["parkedReason"] = "StartedUnlicensed" };
        internal readonly List<string> Requests = [];
        internal string? Published;
        internal string? Unavailable;
        internal Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? Handler;

        public string Workspace => "/fake";
        public bool SupportsCommand(string command) => false;
        public void SetProgress(string key, string? message) { }
        public void Track(string key, string describe, Func<CancellationToken, Task> work) { }
        public bool Contains(string path) => false;
        public void SetState(string? state) => Published = state;
        public void SetUnavailable(string? reason) => Unavailable = reason;

        internal void Push(JsonObject state)
        {
            State = state;
            IntelliJLicensing.OnMessage(this, IntelliJLicensing.StateNotification, state.DeepClone());
        }

        public async Task<JsonNode?> RequestAsync(string method, object? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            Requests.Add(method + (parameters is null ? "" : " " + System.Text.Json.JsonSerializer.Serialize(parameters)));
            var node = parameters is null ? null : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(parameters));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
            return await Handler!(method["jetbrains/licensing/".Length..], node, deadline.Token);
        }
    }

    private static JsonObject Licensed(string source, int? days = null, string status = "Valid") => new()
    {
        ["activeLicense"] = new JsonObject { ["source"] = source, ["status"] = status, ["product"] = "intellij-server", ["licensedTo"] = "Ada", ["daysLeft"] = days, ["validThrough"] = "2026-12-02", ["assetId"] = "asset-1" }
    };

    private static JsonObject View(FakeServer server) => (JsonObject)JsonNode.Parse(server.Published!)!;

    private static async Task<JsonObject> Run(FakeServer server, string name, JsonObject? arguments = null, CancellationToken token = default) =>
        (JsonObject)(await IntelliJLicensing.ActionAsync(server, name, arguments, token))!;

    internal static async Task Run()
    {
        var saved = IntelliJLicensing.EnvironmentLicense();
        Require(saved is null, "The check needs an environment without an environment license; unset " + saved + ".");
        var server = new FakeServer();
        var fileCode = "0FILECODE0-" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"licenseId\":\"0FILECODE0\",\"products\":[]}")) + "-QUJDREVGRw==-U1lOVEhFVElD";
        var olderCode = "0OLDERCODE-" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"licenseId\":\"0OLDERCODE\",\"products\":[]}")) + "-QUJDREVGRw==-U1lOVEhFVElD";
        var sentCodes = new List<string>();
        server.Handler = (method, arguments, token) => Task.FromResult<JsonNode?>(method switch
        {
            "discovery/autoActivate" => new JsonObject { ["activated"] = false },
            "state/get" => server.State.DeepClone(),
            "trial/startAnonymous" => Start(),
            "activationCode/use" => Code(arguments),
            "jba/logout" => Logout(),
            "licenseServer/add" => AddServer(arguments),
            "licenseServer/reconnect" => new JsonObject { ["status"] = "Connected" },
            "licenseServer/remove" => Removed(),
            "activationCode/disable" => Removed(),
            "subscriptions/unuseAll" => Removed(),
            "subscriptions/use" => Use(arguments),
            _ => throw new IOException("Unexpected request " + method)
        });
        JsonNode? Start() { server.Push(Licensed("trial", 30)); return new JsonObject { ["isSuccess"] = true }; }
        JsonNode? Code(JsonNode? arguments) { var sent = arguments?["activationCode"]?.ToString(); sentCodes.Add(sent ?? ""); if (sent != "ABC-123" && sent != fileCode && sent != olderCode) return false; server.Push(Licensed("activationCode", 300)); return true; }
        JsonNode? Logout() { var next = (JsonObject)server.State.DeepClone(); next["jbaLicensingState"] = new JsonObject { ["authenticationStatus"] = "NotAuthenticated" }; server.Push(next); return null; }
        JsonNode? AddServer(JsonNode? arguments) { server.Push(new JsonObject { ["activeLicense"] = new JsonObject { ["source"] = "licenseServer", ["status"] = "Valid", ["product"] = "intellij-server", ["serverAddress"] = arguments!["serverAddress"]!.ToString() }, ["licenseServerFlowState"] = new JsonObject { ["kind"] = "configured", ["serverAddress"] = arguments["serverAddress"]!.ToString(), ["floatingTicketStatus"] = "Obtained" } }); return new JsonObject { ["status"] = "Connected" }; }
        JsonNode? Removed() { server.Push(new JsonObject { ["featuresDeclined"] = true, ["parkedReason"] = "LicenseRemoved" }); return null; }
        JsonNode? Use(JsonNode? arguments) { var next = Licensed("subscription", 200); next["activeLicense"]!["assetId"] = arguments!["assetId"]!.ToString(); server.Push(next); return null; }

        // Unlicensed: the state is surfaced, and the session answers with the reason instead of silence.
        server.Push(server.State);
        Require(View(server)["kind"]!.ToString() == "parked" && server.Unavailable is { } reason && reason.Contains("No active license", StringComparison.Ordinal), "An unlicensed server was not held back with a reason.");
        await Task.Delay(100);
        Require(server.Requests.Contains("jetbrains/licensing/discovery/autoActivate {}"), "A server without a license was not offered license discovery.");

        // Trial start, then the days left and the end-of-trial notice.
        var started = await Run(server, "trial");
        Require(started["ok"]!.GetValue<bool>() && View(server)["kind"]!.ToString() == "licensed" && View(server)["daysLeft"]!.GetValue<int>() == 30 && server.Unavailable is null && View(server)["notice"] is null, "A started trial did not release the hold.");
        server.Push(Licensed("trial", 3));
        Require(View(server)["notice"]!.ToString().Contains("3 days", StringComparison.Ordinal), "A trial ending soon showed no notice.");
        server.Push(Licensed("trial", 0));
        Require(View(server)["notice"]!.ToString().Contains("today", StringComparison.Ordinal), "The last trial day showed no notice.");

        // Grace keeps features but warns; an expired license without grace is unlicensed and holds answers back.
        server.Push(new JsonObject { ["activeLicense"] = Licensed("subscription", 0, "Expired")["activeLicense"]!.DeepClone(), ["graceDeadline"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds() });
        Require(View(server)["kind"]!.ToString() == "grace" && server.Unavailable is null && View(server)["notice"] is not null, "Grace blocked features or hid its notice.");
        server.Push(new JsonObject { ["activeLicense"] = Licensed("subscription", 0, "Expired")["activeLicense"]!.DeepClone() });
        Require(View(server)["kind"]!.ToString() == "unlicensed" && server.Unavailable is not null, "An expired license without grace was not held back.");
        server.Push(new JsonObject { ["activeLicense"] = Licensed("subscription", 40)["activeLicense"]!.DeepClone(), ["graceDeadline"] = 1L });
        Require(View(server)["kind"]!.ToString() == "licensed" && View(server)["graceDeadline"] is null, "A valid license kept a contradictory grace deadline.");

        // Activation code: whitespace is removed, a rejected code is reported, an accepted one licenses.
        server.Push(new JsonObject { ["featuresDeclined"] = true });
        var rejected = await Run(server, "code", new() { ["activationCode"] = "nope" });
        Require(!rejected["ok"]!.GetValue<bool>() && rejected["message"]!.ToString().Contains("isn't valid", StringComparison.Ordinal), "A rejected code was not reported.");
        var accepted = await Run(server, "code", new() { ["activationCode"] = " ABC -\n123 " });
        Require(accepted["ok"]!.GetValue<bool>() && View(server)["source"]!.ToString() == "activationCode", "An activation code did not license.");
        // The downloaded offline activation zip: the host reads the code of its "for 2018.2 or later" entry and sends it as an activation code.
        server.Push(new JsonObject { ["featuresDeclined"] = true });
        var scratch = Path.Combine(Path.GetTempPath(), "tereditor-license-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var zipPath = Path.Combine(scratch, "0FILECODE0 - ada@example.com.zip");
            // The portal's layout with made-up codes: two text files whose names hold spaces, " - ", an "@" and dots, the older one listed first.
            void MakeZip(string path, params (string Name, string Text)[] entries)
            {
                using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
                foreach (var (name, text) in entries) using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(text);
            }
            MakeZip(zipPath, ("0FILECODE0 - ada.l@example.com - for 2018.1 or earlier.txt", olderCode), ("0FILECODE0 - ada.l@example.com - for 2018.2 or later.txt", fileCode + "\n"));
            var fromZip = await Run(server, "codeFile", new() { ["licenseFile"] = zipPath });
            Require(fromZip["ok"]!.GetValue<bool>() && View(server)["source"]!.ToString() == "activationCode" && sentCodes[^1] == fileCode, "A license zip did not send the entry for 2018.2 or later.");
            // The path may come quoted, as a file:// address or with the entry names in other case; the older entry is used only when it is all the zip has.
            sentCodes.Clear();
            Require((await Run(server, "codeFile", new() { ["licenseFile"] = "  \"" + zipPath + "\" " }))["ok"]!.GetValue<bool>() && (await Run(server, "codeFile", new() { ["licenseFile"] = new Uri(zipPath).AbsoluteUri }))["ok"]!.GetValue<bool>()
                && sentCodes.SequenceEqual([fileCode, fileCode]), "A quoted path or a file address was not read.");
            Require(IntelliJLicenseMount.NormalizePath("~/Downloads/a b.zip") == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads/a b.zip"), "A path starting with ~ was not expanded.");
            var shouting = Path.Combine(scratch, "upper.zip");
            MakeZip(shouting, ("A - b@c.d - FOR 2018.1 OR EARLIER.TXT", olderCode), ("A - b@c.d - FOR 2018.2 OR LATER.TXT", fileCode));
            sentCodes.Clear();
            Require((await Run(server, "codeFile", new() { ["licenseFile"] = shouting }))["ok"]!.GetValue<bool>() && sentCodes.SequenceEqual([fileCode]), "Entry names in capitals were not matched.");
            var onlyOlder = Path.Combine(scratch, "older.zip");
            MakeZip(onlyOlder, ("A - b@c.d - for 2018.1 or earlier.txt", olderCode));
            sentCodes.Clear();
            Require((await Run(server, "codeFile", new() { ["licenseFile"] = onlyOlder }))["ok"]!.GetValue<bool>() && sentCodes.SequenceEqual([olderCode]), "A zip with only the older entry was not used.");
            var bare = Path.Combine(scratch, "bare.zip");
            MakeZip(bare, ("license.txt", fileCode));
            sentCodes.Clear();
            Require((await Run(server, "codeFile", new() { ["licenseFile"] = bare }))["ok"]!.GetValue<bool>() && sentCodes.SequenceEqual([fileCode]), "A zip with one bare text file was not used.");
            var empty = Path.Combine(scratch, "empty.zip");
            MakeZip(empty, ("readme.md", fileCode));
            sentCodes.Clear();
            var none = await Run(server, "codeFile", new() { ["licenseFile"] = empty });
            Require(!none["ok"]!.GetValue<bool>() && none["message"]!.ToString().Contains("no text file", StringComparison.Ordinal) && sentCodes.Count == 0 && !none["message"]!.ToString().Contains(fileCode, StringComparison.Ordinal), "A zip without a text file was not refused plainly.");
            var missing = await Run(server, "codeFile", new() { ["licenseFile"] = Path.Combine(scratch, "nope.zip") });
            Require(!missing["ok"]!.GetValue<bool>() && missing["message"]!.ToString().Contains("does not exist", StringComparison.Ordinal), "A missing license file was not reported.");
            var notes = Path.Combine(scratch, "notes.txt");
            File.WriteAllText(notes, "not a code");
            var wrong = await Run(server, "codeFile", new() { ["licenseFile"] = notes });
            Require(!wrong["ok"]!.GetValue<bool>() && !wrong["message"]!.ToString().Contains("not a code", StringComparison.Ordinal) && !wrong["message"]!.ToString().Contains(fileCode, StringComparison.Ordinal), "A file that is not a code was accepted or echoed.");
        }
        finally { Directory.Delete(scratch, true); }
        var removed = await Run(server, "removeLicense");
        Require(removed["ok"]!.GetValue<bool>() && server.Requests.Last().StartsWith("jetbrains/licensing/activationCode/disable", StringComparison.Ordinal) && View(server)["kind"]!.ToString() == "parked", "Removing an activation-code license used the wrong request.");

        // License server: URL validation, connect, reconnect, remove.
        Require(!(await Run(server, "serverAdd", new() { ["serverAddress"] = "ftp://x" }))["ok"]!.GetValue<bool>(), "A non-http license server address was accepted.");
        Require((await Run(server, "serverAdd", new() { ["serverAddress"] = "https://licenses.example.com" }))["ok"]!.GetValue<bool>() && View(server)["serverAddress"]!.ToString() == "https://licenses.example.com", "A license server did not connect.");
        Require((await Run(server, "serverReconnect"))["ok"]!.GetValue<bool>(), "Reconnect failed.");
        Require((await Run(server, "removeLicense"))["ok"]!.GetValue<bool>() && server.Requests.Last().Contains("licenseServer/remove", StringComparison.Ordinal), "Removing a license-server license used the wrong request.");

        // JetBrains Account: the page is published for the client to open, a single valid subscription is used, several are offered.
        var subscription = (string id, string status) => new JsonObject { ["assetId"] = id, ["product"] = "intellij-server", ["licensedTo"] = "Ada", ["status"] = status, ["validThrough"] = "2027-01-01" };
        var login = new TaskCompletionSource<JsonNode?>();
        server.Handler = (method, arguments, token) => method switch
        {
            "jba/login" => login.Task.WaitAsync(token),
            "jba/logout" => Task.FromResult<JsonNode?>(Logout()),
            "subscriptions/use" => Task.FromResult<JsonNode?>(Use(arguments)),
            _ => Task.FromResult<JsonNode?>(null)
        };
        var signingIn = Run(server, "signIn");
        await Task.Delay(50);
        server.Push(new JsonObject { ["featuresDeclined"] = true, ["jbaLicensingState"] = new JsonObject { ["authenticationStatus"] = "Authenticating" } });
        IntelliJLicensing.OnMessage(server, "window/showDocument", new JsonObject { ["uri"] = "https://account.jetbrains.com/sign-in", ["external"] = true });
        Require(View(server)["busy"]!.ToString() == "signIn" && View(server)["page"]!["uri"]!.ToString().StartsWith("https://account.jetbrains.com", StringComparison.Ordinal) && View(server)["kind"]!.ToString() == "authenticating", "The sign-in page and waiting state were not surfaced.");
        server.Push(new JsonObject { ["featuresDeclined"] = true, ["jbaLicensingState"] = new JsonObject { ["authenticationStatus"] = "Authenticated", ["accountInfo"] = new JsonObject { ["displayName"] = "Ada" }, ["availableSubscriptions"] = new JsonArray(subscription("asset-1", "Valid"), subscription("asset-2", "Expired")) } });
        login.SetResult(new JsonObject { ["status"] = "NoSuitableLicense" });
        var signedIn = await signingIn;
        Require(signedIn["ok"]!.GetValue<bool>() && View(server)["kind"]!.ToString() == "licensed" && View(server)["busy"]!.ToString() == "" && View(server)["page"] is null, "A sign-in with one valid subscription did not license.");
        server.Push(new JsonObject { ["featuresDeclined"] = true, ["jbaLicensingState"] = new JsonObject { ["authenticationStatus"] = "Authenticated", ["availableSubscriptions"] = new JsonArray(subscription("asset-1", "Valid"), subscription("asset-3", "Valid")) } });
        login = new();
        login.SetResult(new JsonObject { ["status"] = "NoSuitableLicense" });
        var several = await Run(server, "signIn");
        Require(!several["ok"]!.GetValue<bool>() && View(server)["subscriptions"]!.AsArray().Count == 2, "Several subscriptions were not offered.");
        Require((await Run(server, "useSubscription", new() { ["assetId"] = "asset-3" }))["ok"]!.GetValue<bool>(), "A chosen subscription did not activate.");

        // Cancelling a sign-in stops waiting and signs out so the server ends its wait in the browser.
        server.Push(new JsonObject { ["featuresDeclined"] = true });
        login = new();
        using (var cancel = new CancellationTokenSource())
        {
            var pending = Run(server, "signIn", token: cancel.Token);
            await Task.Delay(50);
            var before = server.Requests.Count;
            cancel.Cancel();
            try { await pending; throw new InvalidOperationException("A cancelled sign-in completed."); }
            catch (OperationCanceledException) { }
            Require(server.Requests.Skip(before).Any(request => request.StartsWith("jetbrains/licensing/jba/logout", StringComparison.Ordinal)) && View(server)["busy"]!.ToString() == "", "Cancelling a sign-in did not end the server's wait.");
        }

        // A request the server never answers times out with a message instead of hanging; the sign-in waits longer than the server's five minutes.
        Require(IntelliJLicensing.Timeouts.Browser > TimeSpan.FromMinutes(5), "The sign-in must outlast the server's five-minute wait.");
        var shortened = IntelliJLicensing.Timeouts.Medium;
        IntelliJLicensing.Timeouts.Medium = TimeSpan.FromMilliseconds(100);
        try
        {
            server.Handler = async (method, arguments, token) => { await Task.Delay(Timeout.Infinite, token); return null; };
            var late = await Run(server, "trial");
            Require(!late["ok"]!.GetValue<bool>() && late["message"]!.ToString().Contains("did not answer", StringComparison.Ordinal), "A silent server was not reported.");
        }
        finally { IntelliJLicensing.Timeouts.Medium = shortened; }

        // A license set in the environment overrides activation from here.
        Environment.SetEnvironmentVariable("INTELLIJ_SERVER_LICENSE", "fixture");
        try
        {
            var blocked = await Run(server, "trial");
            Require(!blocked["ok"]!.GetValue<bool>() && blocked["message"]!.ToString().Contains("INTELLIJ_SERVER_LICENSE", StringComparison.Ordinal), "An environment license was not explained.");
        }
        finally { Environment.SetEnvironmentVariable("INTELLIJ_SERVER_LICENSE", null); }

        try { await Run(server, "nonsense"); throw new InvalidOperationException("An unknown license action ran."); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS IntelliJ license state, hold, trial/sign-in/code/license-server/remove actions, cancellation, timeout and environment license against a fake licensing server");
    }

    private static void Submit(Tereditor.UI.Panels.LicenseActivationWindow dialog, int kind, string value)
    {
        Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.ComboBox>(dialog, "ActivationKind")!.SelectedIndex = kind;
        Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBox>(dialog, "ActivationInput")!.Text = value;
        Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(dialog, "ActivationSubmit")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    }

    /// <summary>The settings block: state, days left, the trial-ending notice, the offered actions and the call a click makes.</summary>
    internal static void Ui(string fixture)
    {
        using var app = new E2E.E2eWorkspace(Path.Combine(fixture, "intellij-license-ui"), openFiles: false);
        E2E.E2eWorkspace.Until(() => app.State!.Current.Workspaces.Any(item => item.Path == app.Root));
        var calls = new List<LanguageRequest>();
        string? rowState = null;
        var actionFails = false;
        app.Host.LanguageReply = request =>
        {
            if (request.Operation == "servers" && rowState is not null)
                return new("ready", "") { Servers = [new("intellij", "", "ready", "Ready", "263.7084.0") { PluginState = rowState, Presentation = new("Java / Kotlin (IntelliJ)") }] };
            if (request.Operation != "action") return null;
            calls.Add(request);
            return new("ready", actionFails ? """{"ok":false,"message":"That activation code isn't valid. Check the code and try again."}""" : """{"ok":true,"message":null}""");
        };
        var trial = Licensing(new JsonObject { ["activeLicense"] = new JsonObject { ["source"] = "trial", ["status"] = "Valid", ["product"] = "intellij-server", ["daysLeft"] = 3, ["validThrough"] = "2026-11-05" } });
        var view = new Tereditor.UI.Panels.LanguageConfigurationView(app.Host, app.Root, new("intellij", "", "ready", "Ready", "263.7084.0") { PluginState = trial });
        var window = new Avalonia.Controls.Window { Width = 700, Height = 900, Content = view };
        window.Show(app.Window);
        try
        {
            T Find<T>(string name) where T : Avalonia.Controls.Control => Avalonia.Controls.ControlExtensions.FindControl<T>(view, name)!;
            Require(Find<Avalonia.Controls.StackPanel>("LicensePanel").IsVisible, "The license block was hidden for a licensed IntelliJ.");
            Require(Find<Avalonia.Controls.TextBlock>("LicenseState").Text!.Contains("3\u00A0days left (valid through 2026-11-05)", StringComparison.Ordinal), "Trial days left were not shown: " + Find<Avalonia.Controls.TextBlock>("LicenseState").Text);
            Require(Find<Avalonia.Controls.TextBlock>("LicenseNotice") is { IsVisible: true } notice && notice.Text!.Contains("3 days", StringComparison.Ordinal), "A trial ending soon showed no notice.");
            view.Update(new("intellij", "", "actionRequired", "No active license.", "263.7084.0") { PluginState = Licensing(new JsonObject { ["featuresDeclined"] = true }) });
            Require(Find<Avalonia.Controls.TextBlock>("LicenseState").Text!.Contains("No active license", StringComparison.Ordinal) && Find<Avalonia.Controls.TextBlock>("LicenseNotice").IsVisible, "An unlicensed server did not say so.");
            Require(!Find<Avalonia.Controls.Button>("RemoveLicense").IsVisible && Find<Avalonia.Controls.Button>("StartTrial").IsEnabled && Find<Avalonia.Controls.Button>("SignIn").IsEnabled, "Unlicensed actions were not offered.");
            // The host answers the inventory read that follows an action with the state it holds.
            rowState = Licensing(new JsonObject { ["featuresDeclined"] = true });
            Find<Avalonia.Controls.Button>("StartTrial").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => calls.Count > 0 && Find<Avalonia.Controls.TextBlock>("ConfigurationMessage").Text == "License updated.");
            Require(calls[0].Server == "intellij" && calls[0].Query == "trial", "Start trial did not request the trial action.");
            // Enter activation code opens the shared window. A code the host refuses shows its error and keeps the window; a good one reaches the host, closes it and refreshes the state.
            var enter = Find<Avalonia.Controls.Button>("EnterCode");
            E2E.E2eWorkspace.Until(() => enter.IsEnabled, "Enter activation code stayed disabled after the trial.");
            Require(enter.IsEnabled && enter.Content?.ToString() == "Enter activation code…", "The unlicensed block did not offer Enter activation code.");
            Tereditor.UI.Panels.LicenseActivationWindow Opened()
            {
                enter.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                E2E.E2eWorkspace.Until(() => Tereditor.UI.Panels.LicenseActivationWindow.Current is not null, "Enter activation code opened no window.");
                return Tereditor.UI.Panels.LicenseActivationWindow.Current!;
            }
            actionFails = true;
            var code = Opened();
            Submit(code, 0, " AB-12 ");
            E2E.E2eWorkspace.Until(() => calls.Count > 1 && Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(code, "ActivationError")!.IsVisible, "A refused code showed no error.");
            Require(calls[1].Query == "code" && JsonNode.Parse(calls[1].Configuration!)!["activationCode"]!.ToString() == " AB-12 ", "The activation code was not sent.");
            Require(Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(code, "ActivationError")!.Text!.Contains("isn't valid", StringComparison.Ordinal) && Tereditor.UI.Panels.LicenseActivationWindow.Current == code, "A refused code lost its error or closed the window.");
            actionFails = false;
            rowState = Licensing(new JsonObject { ["activeLicense"] = new JsonObject { ["source"] = "activationCode", ["status"] = "Valid", ["product"] = "intellij-server", ["daysLeft"] = 90, ["validThrough"] = "2027-02-01" } });
            Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(code, "ActivationSubmit")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => Tereditor.UI.Panels.LicenseActivationWindow.Current is null && Find<Avalonia.Controls.TextBlock>("LicenseState").Text!.Contains("90", StringComparison.Ordinal), "A good code did not close the window and refresh the state.");
            Require(Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBox>(code, "ActivationInput")!.Text == "", "The code stayed in the window after it closed.");
            // A file dropped on the window is activated as the license file at once.
            var dropped = Opened();
            _ = dropped.DropFileAsync("/Users/ada/Downloads/portal.zip");
            E2E.E2eWorkspace.Until(() => calls.Count > 3 && calls[3].Query == "codeFile", "A dropped file was not sent as a license file.");
            Require(JsonNode.Parse(calls[3].Configuration!)!["licenseFile"]!.ToString() == "/Users/ada/Downloads/portal.zip", "The dropped file path was not sent.");
            E2E.E2eWorkspace.Until(() => Tereditor.UI.Panels.LicenseActivationWindow.Current is null, "A dropped file did not close the window.");
            // Browse and drop are for a local host; a remote host keeps the typed path.
            foreach (var local in new[] { true, false })
            {
                var probe = new Tereditor.UI.Panels.LicenseActivationWindow((_, _, _) => Task.FromResult<string?>(null), local);
                Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.ComboBox>(probe, "ActivationKind")!.SelectedIndex = 1;
                Require(Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(probe, "ActivationBrowse")!.IsVisible == local && Avalonia.Input.DragDrop.GetAllowDrop(probe) == local, "Browse and drop were not limited to a local host.");
                probe.Close();
            }
            // The offline activation zip and a license server go through the same host action, each with its own argument.
            var file = Opened();
            Submit(file, 1, "/license/offline.zip");
            E2E.E2eWorkspace.Until(() => calls.Count > 4, "A license file was not sent.");
            Require(calls[4].Query == "codeFile" && JsonNode.Parse(calls[4].Configuration!)!["licenseFile"]!.ToString() == "/license/offline.zip", "The license file path was not sent as codeFile.");
            var licenseServer = Opened();
            Submit(licenseServer, 2, "https://licenses.example.com");
            E2E.E2eWorkspace.Until(() => calls.Count > 5, "A license server was not sent.");
            Require(calls[5].Query == "serverAdd" && JsonNode.Parse(calls[5].Configuration!)!["serverAddress"]!.ToString() == "https://licenses.example.com", "The license server address was not sent as serverAdd.");
            // The Code Intelligence row itself shows the notice, without opening the settings.
            rowState = trial;
            var pane = new Tereditor.UI.Panels.LanguagePanel(app.Host, app.Root, _ => Task.CompletedTask, app.Workbench.State);
            var paneWindow = new Avalonia.Controls.Window { Width = 520, Height = 700, Content = pane };
            paneWindow.Show();
            try
            {
                E2E.E2eWorkspace.Until(() =>
                {
                    if (pane.Idle) pane.RefreshNow();
                    return Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(pane).OfType<Avalonia.Controls.TextBlock>()
                        .Any(text => text.Name == "ServerNotice" && text.IsEffectivelyVisible && (text.Text ?? "").Contains("3 days", StringComparison.Ordinal));
                }, "The Code Intelligence row did not show the trial notice.");
                // A row without a usable license offers the same window; the host call it makes refreshes the row.
                Avalonia.Controls.Button? Activate() => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(pane).OfType<Avalonia.Controls.Button>().FirstOrDefault(button => button.Name == "ServerActivateAction" && button.IsEffectivelyVisible);
                Require(Activate() is null, "A licensed row offered to activate a license.");
                rowState = Licensing(new JsonObject { ["featuresDeclined"] = true });
                E2E.E2eWorkspace.Until(() =>
                {
                    if (pane.Idle) pane.RefreshNow();
                    return Activate() is not null;
                }, "An unlicensed row did not offer Enter activation code.");
                Activate()!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                E2E.E2eWorkspace.Until(() => Tereditor.UI.Panels.LicenseActivationWindow.Current is not null, "The row's Enter activation code opened no window.");
                var rowDialog = Tereditor.UI.Panels.LicenseActivationWindow.Current!;
                var before = calls.Count;
                rowState = trial;
                Submit(rowDialog, 0, "ROW-1");
                E2E.E2eWorkspace.Until(() => calls.Count == before + 1 && Tereditor.UI.Panels.LicenseActivationWindow.Current is null && Activate() is null, "The row's activation did not reach the host, close the window and refresh the row.");
                Require(calls[^1].Query == "code" && calls[^1].Server == "intellij", "The row sent the wrong action.");
            }
            finally { paneWindow.Close(); }
            var plain = new Tereditor.UI.Panels.LanguageConfigurationView(app.Host, app.Root, new("typescript", "", "idle", "Fixture", "fixture"));
            Require(!Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.StackPanel>(plain, "LicensePanel")!.IsVisible, "A server without license state showed a license block.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS IntelliJ settings license block: state, trial days and notice, unlicensed message, actions and their host calls");
    }

    /// <summary>The terms status is three-state: unknown shows no button, only a known state offers review, and the last known acceptance is cached per server version.</summary>
    internal static void TermsStatus()
    {
        var checking = Tereditor.UI.Panels.TermsStatusView.Of(Tereditor.UI.Panels.TermsKnowledge.Checking);
        Require(!checking.ShowReview && !checking.ReviewEnabled && !checking.Text.Contains("not accepted", StringComparison.OrdinalIgnoreCase), "The unknown state must show no button and not claim the terms are not accepted: " + checking.Text);
        var notAccepted = Tereditor.UI.Panels.TermsStatusView.Of(Tereditor.UI.Panels.TermsKnowledge.NotAccepted);
        Require(notAccepted.ShowReview && notAccepted.ReviewEnabled && notAccepted.Text.StartsWith("Terms not accepted", StringComparison.Ordinal), "A known not-accepted state must offer Review and accept terms.");
        var accepted = Tereditor.UI.Panels.TermsStatusView.Of(Tereditor.UI.Panels.TermsKnowledge.Accepted, "2026-11-07", "263.7084.0");
        Require(accepted.Text == "Terms accepted on 2026-11-07 (version 263.7084.0)." && !accepted.ReviewEnabled, "The accepted state must show the date and version: " + accepted.Text);
        const string server = "terms-cache-fixture";
        Require(!Tereditor.UI.Panels.TermsAcceptanceCache.TryGet(server, "1", out _, out _), "An empty cache returned an acceptance.");
        Tereditor.UI.Panels.TermsAcceptanceCache.Store(server, "1", "2026-11-07", "263.7084.0");
        Require(Tereditor.UI.Panels.TermsAcceptanceCache.TryGet(server, "1", out var at, out var version) && at == "2026-11-07" && version == "263.7084.0", "The cache did not return the stored acceptance.");
        Require(!Tereditor.UI.Panels.TermsAcceptanceCache.TryGet(server, "2", out _, out _), "A new server version must invalidate the cached acceptance.");
        Require(!Tereditor.UI.Panels.TermsAcceptanceCache.TryGet(server, "1", out _, out _), "The invalidated acceptance came back.");
        Tereditor.UI.Panels.TermsAcceptanceCache.Store(server, "1", "2026-11-07", "");
        Tereditor.UI.Panels.TermsAcceptanceCache.Forget(server);
        Require(!Tereditor.UI.Panels.TermsAcceptanceCache.TryGet(server, "1", out _, out _), "Forget kept the acceptance.");
        Console.WriteLine("PASS Settings terms status: unknown shows no button, known states offer review, acceptance cache invalidates on a new server version");
    }

    /// <summary>Settings › Code Intelligence: a server with terms shows their status, a review button, and Install now once accepted; one without shows none of it.</summary>
    internal static void SettingsTerms(string fixture)
    {
        E2E.E2eWorkspace? holder = null;
        using var app = holder = new E2E.E2eWorkspace(Path.Combine(fixture, "settings-terms"), openFiles: false, hostPlugins: FakeCodeIntelligence.HostPlugins(() => holder!.Host));
        var terms = new List<LanguageTermsRequest>();
        var provisions = new List<LanguageRequest>();
        var accepted = false;
        var installed = false;
        var failInstall = true;
        const string Hash = "0123456789012345678901234567890123456789012345678901234567890123";
        app.Host.TermsReply = request =>
        {
            terms.Add(request);
            if (request.Accept) accepted = true;
            return new(accepted ? "accepted" : "requiresAcceptance", "Fixture", "263.7084.0", Hash, Hash, "Fixture terms", accepted, accepted ? "2026-11-07T10:00:00.0000000+00:00" : "");
        };
        app.Host.LanguageReply = request =>
        {
            if (request.Operation == "catalog")
                return new("ready", "storage")
                {
                    Servers =
                    [
                        new("intellij", "", "available", "IntelliJ Java and Kotlin · JetBrains", "263.7084.0") { Presentation = new("Java / Kotlin (IntelliJ)", HasTerms: true) },
                        new("typescript", "", "available", "TypeScript", "5.0") { Presentation = new("TypeScript") }
                    ]
                };
            if (request.Operation == "resolution")
                return new(installed ? "ready" : "unavailable", installed ? "Ready" : "Not installed yet", request.Server)
                { Configuration = new JsonObject { ["path"] = installed ? "/fixture/idea" : "", ["version"] = "", ["warning"] = "", ["source"] = installed ? "managed" : "none", ["pathCapable"] = false, ["pinning"] = "" }.ToJsonString() };
            if (request.Operation == "provision")
            {
                provisions.Add(request);
                if (failInstall) return new("error", "Fixture download failed", request.Server);
                installed = true;
                return new("ready", "Installed.", request.Server);
            }
            return null;
        };
        var settings = new Tereditor.UI.Panels.SettingsView(app.Window, () => { }, section: "Code Intelligence");
        app.Window.ShowInOverlay(settings);
        try
        {
            T Find<T>(string name) where T : Avalonia.Controls.Control => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(settings).OfType<T>().Single(control => control.Name == name);
            var rail = Find<Avalonia.Controls.ListBox>("LspServer");
            E2E.E2eWorkspace.Until(() => rail.ItemCount == 2, "The settings rail did not list the fixture catalog.");
            void Select(string id) => rail.SelectedItem = rail.Items.Cast<object>().First(item => item?.ToString() == id);
            Select("typescript");
            Require(!Find<Avalonia.Controls.StackPanel>("LspTermsSection").IsVisible, "A server without terms showed the terms controls.");
            Select("intellij");
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.StackPanel>("LspTermsSection").IsVisible, "A server with terms showed no terms controls.");
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.TextBlock>("LspTermsStatus").Text!.StartsWith("Terms not accepted", StringComparison.Ordinal), "The unaccepted status was not shown: " + Find<Avalonia.Controls.TextBlock>("LspTermsStatus").Text);
            Require(Find<Avalonia.Controls.Button>("LspReviewTerms").IsEnabled && !Find<Avalonia.Controls.Button>("LspInstall").IsVisible, "Before acceptance the review button must be on and Install now hidden.");
            Require(terms.Count == 0, "Selecting a server that is not installed read its terms, which downloads it.");
            Find<Avalonia.Controls.Button>("LspReviewTerms").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Avalonia.Controls.Window? OpenTerms() => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(settings).OfType<Tereditor.Plugins.CodeIntelligence.UI.CodeIntelligencePage>().Single().TermsWindow;
            E2E.E2eWorkspace.Until(() => OpenTerms() is not null, "The review button opened no terms window.");
            var dialog = OpenTerms()!;
            E2E.E2eWorkspace.Until(() => terms.Count > 0 && Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.CheckBox>(dialog, "TermsConsent")!.IsEnabled, "The terms window did not load the terms.");
            Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.CheckBox>(dialog, "TermsConsent")!.IsChecked = true;
            E2E.E2eWorkspace.Until(() => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(dialog, "AcceptTerms")!.IsEnabled);
            Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(dialog, "AcceptTerms")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => terms.Any(request => request.Accept && request.Server == "intellij" && request.ArtifactId == Hash && request.EulaSha256 == Hash), "Accepting did not record the acknowledgement through the host call.");
            E2E.E2eWorkspace.Until(() => dialog is Tereditor.Plugins.CodeIntelligence.UI.LanguageTermsWindow { Result.Accepted: true }, "The terms window did not take the host's accepted reply before closing.");
            dialog.Close();
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.TextBlock>("LspTermsStatus").Text!.StartsWith("Terms accepted on 2026-11-07", StringComparison.Ordinal), "The accepted status was not shown.");
            Require(!Find<Avalonia.Controls.Button>("LspReviewTerms").IsEnabled, "The review button stayed on after acceptance.");
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.TextBlock>("LspResolved").Text!.StartsWith("None", StringComparison.Ordinal) && Find<Avalonia.Controls.Button>("LspInstall").IsVisible, "Install now was not offered after acceptance.");
            Find<Avalonia.Controls.Button>("LspInstall").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => provisions.Count == 1 && Find<Avalonia.Controls.TextBlock>("LspInstallStatus").Text!.Contains("Fixture download failed", StringComparison.Ordinal) && Find<Avalonia.Controls.Button>("LspInstallRetry").IsVisible, "A failed install showed no error and Retry.");
            Require(provisions[0].Server == "intellij" && provisions[0].Operation == "provision", "Install now did not call the provision path for the server.");
            failInstall = false;
            Find<Avalonia.Controls.Button>("LspInstallRetry").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => provisions.Count == 2 && Find<Avalonia.Controls.TextBlock>("LspInstallStatus").Text == "Installed." && !Find<Avalonia.Controls.Button>("LspInstall").IsVisible, "Retry did not install.");
            Select("typescript");
            Select("intellij");
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.TextBlock>("LspTermsStatus").Text!.StartsWith("Terms accepted on", StringComparison.Ordinal), "An installed, accepted server did not show its recorded acceptance.");
        }
        finally { Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(settings).OfType<Tereditor.Plugins.CodeIntelligence.UI.CodeIntelligencePage>().FirstOrDefault()?.TermsWindow?.Close(); }
        Console.WriteLine("PASS Settings terms: status, review dialog records acceptance via the host, Install now with error and Retry, nothing for a server without terms");
        TermsOutcomes(fixture);
        BlockedStart(fixture);
    }

    /// <summary>
    /// Start on an IntelliJ row that the provider choice or the license terms block never fails silently: the pane says why and offers the fix
    /// inline (use it as the provider, review and accept the terms), accepting through the host lets the server start, and a start the host
    /// refuses shows its error even after the list refreshes.
    /// </summary>
    private static void BlockedStart(string fixture)
    {
        const string Hash = "0123456789012345678901234567890123456789012345678901234567890123";
        E2E.E2eWorkspace? holder = null;
        using var app = holder = new E2E.E2eWorkspace(Path.Combine(fixture, "blocked-start"), openFiles: false, hostPlugins: FakeCodeIntelligence.HostPlugins(() => holder!.Host));
        var provider = "standard";
        var accepted = false;
        var started = new List<LanguageRequest>();
        string? startFailure = null;
        var providerWrites = new List<string>();
        app.Host.TermsReply = request =>
        {
            if (request.Accept) accepted = true;
            return new(accepted ? "accepted" : "requiresAcceptance", "Fixture", "263.7084.0", Hash, Hash, "Fixture terms", accepted, accepted ? "2026-11-07T10:00:00.0000000+00:00" : "");
        };
        app.Host.LanguageReply = request =>
        {
            switch (request.Operation)
            {
                case "servers":
                    return new("ready", "") { Servers = [new("intellij", "/fixture/build.gradle.kts", "idle", "Not started", "263.7084.0") { Presentation = new("Java / Kotlin (IntelliJ)", HasTerms: true, Replaces: ["java", "kotlin"]) }] };
                case "javaKotlinProvider": return new("ready", "") { Configuration = provider };
                case "setJavaKotlinProvider": provider = request.Configuration!; providerWrites.Add(provider); return new("ready", "") { Configuration = provider };
                case "retry": started.Add(request); return startFailure is null ? new("ready", "Started.", request.Server) : new("error", startFailure, request.Server);
                default: return null;
            }
        };
        var pane = new Tereditor.UI.Panels.LanguagePanel(app.Host, app.Root, _ => Task.CompletedTask, app.Workbench.State);
        var window = new Avalonia.Controls.Window { Width = 520, Height = 700, Content = pane };
        window.Show();
        try
        {
            // The notice builds its controls in code, so they are not in the pane's name scope.
            T Find<T>(string name) where T : Avalonia.Controls.Control => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(pane).OfType<T>().Single(control => control.Name == name);
            var servers = Find<Avalonia.Controls.ListBox>("Servers");
            var notice = Find<Tereditor.UI.Panels.LanguageBlockedNotice>("StartNotice");
            E2E.E2eWorkspace.Until(() => servers.ItemCount == 1, "The pane did not list the fixture server.");
            void Start()
            {
                servers.SelectedIndex = 0;
                servers.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            }
            void Click(string name) => Find<Avalonia.Controls.Button>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            // The provider is not chosen: Start says so and offers the choice; the refresh after it does not hide the notice.
            Start();
            E2E.E2eWorkspace.Until(() => notice.IsVisible && Find<Avalonia.Controls.TextBlock>("BlockedMessage").Text!.Contains("not your Java and Kotlin provider", StringComparison.Ordinal), "Start with another provider chosen showed nothing.");
            Require(started.Count == 0 && Find<Avalonia.Controls.Button>("BlockedUseProvider").IsVisible, "A blocked start must not start and must offer the provider choice.");
            pane.RefreshNow();
            E2E.E2eWorkspace.Until(() => pane.Idle);
            Require(notice.IsVisible, "The refresh hid the reason the start was blocked.");
            // Choosing the provider writes it through the host, then Start goes on to the terms and opens their review.
            Click("BlockedUseProvider");
            E2E.E2eWorkspace.Until(() => providerWrites.SequenceEqual(["intellij"]) && notice.IsVisible && Find<Avalonia.Controls.Button>("BlockedReviewTerms").IsVisible && !Find<Avalonia.Controls.Button>("BlockedUseProvider").IsVisible,
                "Choosing the provider did not lead to the terms.");
            Require(started.Count == 0, "The server started before its terms were accepted.");
            Click("BlockedReviewTerms");
            E2E.E2eWorkspace.Until(() => notice.TermsWindow is not null, "Review and accept terms opened no window.");
            var dialog = notice.TermsWindow!;
            E2E.E2eWorkspace.Until(() => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.CheckBox>(dialog, "TermsConsent")!.IsEnabled, "The terms did not load.");
            Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.CheckBox>(dialog, "TermsConsent")!.IsChecked = true;
            E2E.E2eWorkspace.Until(() => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(dialog, "AcceptTerms")!.IsEnabled);
            Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(dialog, "AcceptTerms")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => accepted && dialog is Tereditor.UI.Panels.LanguageTermsWindow { Result.Accepted: true }, "Accepting did not record the terms through the host.");
            E2E.E2eWorkspace.Until(() => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(dialog, "AcceptTerms")!.IsEnabled);
            Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(dialog, "AcceptTerms")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => started.Count == 1 && started[0].Server == "intellij" && !notice.IsVisible, "After accepting, the blocked server did not start.");
            // A start the host refuses shows its error, and the list refresh that follows keeps it.
            startFailure = "Fixture start failed";
            Start();
            E2E.E2eWorkspace.Until(() => started.Count == 2 && Find<Avalonia.Controls.TextBlock>("Status").Text!.Contains("Fixture start failed", StringComparison.Ordinal), "A refused start showed no error: " + Find<Avalonia.Controls.TextBlock>("Status").Text);
            pane.RefreshNow();
            E2E.E2eWorkspace.Until(() => pane.Idle);
            Require(Find<Avalonia.Controls.TextBlock>("Status").Text!.Contains("Fixture start failed", StringComparison.Ordinal), "The refresh wiped the start error.");
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
        }
        Console.WriteLine("PASS Blocked start: the pane names the provider or terms block, offers the fix inline, accepting starts the server, a refused start keeps its error");
    }

    /// <summary>
    /// The terms row of an installed IntelliJ server leaves "Checking license terms…" for every outcome: accepted, not accepted, a failed read (with Retry),
    /// a read the host gives up on, a host that never answers (the page's own deadline), and a page rebuilt while the read is open.
    /// </summary>
    private static void TermsOutcomes(string fixture)
    {
        const string Hash = "0123456789012345678901234567890123456789012345678901234567890123";
        var result = (bool accepted) => new LanguageTermsResult(accepted ? "accepted" : "requiresAcceptance", "Fixture", "263.7084.0", Hash, Hash, "Fixture terms", accepted, accepted ? "2026-11-07T10:00:00.0000000+00:00" : "");
        void Scenario(string name, TimeSpan hostDeadline, TimeSpan pageTimeout, Action<Func<string>, Func<string, Avalonia.Controls.Button>, E2E.E2eWorkspace, Avalonia.Controls.Control> act, Action<E2E.E2eWorkspace> arrange)
        {
            Tereditor.Plugins.CodeIntelligence.UI.TermsAcceptanceCache.Forget("intellij");
            Tereditor.Plugins.CodeIntelligence.UI.CodeIntelligencePage.TermsTimeout = pageTimeout;
            E2E.E2eWorkspace? holder = null;
            using var app = holder = new E2E.E2eWorkspace(Path.Combine(fixture, "settings-terms-" + name), openFiles: false, hostPlugins: FakeCodeIntelligence.HostPlugins(() => holder!.Host, hostDeadline));
            app.Host.LanguageReply = request => request.Operation switch
            {
                "catalog" => new("ready", "storage") { Servers = [new("intellij", "", "available", "IntelliJ Java and Kotlin · JetBrains", "263.7084.0") { Presentation = new("Java / Kotlin (IntelliJ)", HasTerms: true) }] },
                "resolution" => new("ready", "Ready", request.Server) { Configuration = new JsonObject { ["path"] = "/fixture/idea", ["version"] = "", ["warning"] = "", ["source"] = "managed", ["pathCapable"] = false, ["pinning"] = "" }.ToJsonString() },
                _ => null
            };
            arrange(app);
            var settings = new Tereditor.UI.Panels.SettingsView(app.Window, () => { }, section: "Code Intelligence");
            app.Window.ShowInOverlay(settings);
            try
            {
                T Find<T>(string part) where T : Avalonia.Controls.Control => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(settings).OfType<T>().Single(control => control.Name == part);
                act(() => Find<Avalonia.Controls.TextBlock>("LspTermsStatus").Text ?? "", Find<Avalonia.Controls.Button>, app, settings);
            }
            finally
            {
                Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(settings).OfType<Tereditor.Plugins.CodeIntelligence.UI.CodeIntelligencePage>().FirstOrDefault()?.TermsWindow?.Close();
                Tereditor.Plugins.CodeIntelligence.UI.CodeIntelligencePage.TermsTimeout = TimeSpan.FromSeconds(120);
            }
        }
        var longWait = TimeSpan.FromSeconds(90);
        void StartsWith(Func<string> status, string prefix, string why) => E2E.E2eWorkspace.Until(() => status().StartsWith(prefix, StringComparison.Ordinal), why + " Status: " + status());
        void Plain(string name, bool accepted) => Scenario(name, longWait, longWait,
            (status, button, _, _) =>
            {
                StartsWith(status, accepted ? "Terms accepted on 2026-11-07" : "Terms not accepted", "The row stayed unresolved.");
                Require(!button("LspTermsRetry").IsVisible && button("LspReviewTerms").IsEnabled == !accepted, "A known state must offer no Retry and Review only while not accepted.");
            }, app => app.Host.TermsReply = _ => result(accepted));
        Plain("accepted", true);
        Plain("not-accepted", false);
        Scenario("fails", longWait, longWait, (status, button, app, _) =>
        {
            StartsWith(status, "Terms unavailable: Fixture read failed", "A failed read left the row unresolved.");
            Require(button("LspTermsRetry").IsVisible, "A failed read must offer Retry.");
            app.Host.TermsBefore = null; app.Host.TermsReply = _ => result(true);
            button("LspTermsRetry").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            StartsWith(status, "Terms accepted on 2026-11-07", "Retry did not read the terms again.");
            Require(!button("LspTermsRetry").IsVisible, "Retry stayed visible after the terms were read.");
        }, app => app.Host.TermsBefore = (_, _) => throw new IOException("Fixture read failed"));
        Scenario("host-deadline", TimeSpan.FromSeconds(1), longWait, (status, button, app, _) =>
        {
            StartsWith(status, "Terms unavailable: Reading the license terms took longer than 1 seconds.", "A slow read left the row unresolved after the host's deadline.");
            Require(button("LspTermsRetry").IsVisible, "A read past the host's deadline must offer Retry.");
            app.Host.TermsBefore = null; app.Host.TermsReply = _ => result(false);
            button("LspTermsRetry").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            StartsWith(status, "Terms not accepted", "Retry did not read the terms again.");
        }, app => app.Host.TermsBefore = async (_, token) => await Task.Delay(Timeout.Infinite, token));
        Scenario("page-deadline", longWait, TimeSpan.FromSeconds(1), (status, button, _, _) =>
        {
            StartsWith(status, "Terms unavailable: The host did not answer within 1 seconds.", "A host that never answers left the row unresolved after the page's deadline.");
            Require(button("LspTermsRetry").IsVisible, "A read past the page's deadline must offer Retry.");
        }, app => app.Host.TermsBefore = async (_, _) => await new TaskCompletionSource().Task);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Scenario("rebuilt", longWait, longWait, (status, _, _, settings) =>
        {
            E2E.E2eWorkspace.Until(() => Volatile.Read(ref reads) > 0 && status() == "Checking license terms…", "The first read did not start.");
            var before = Volatile.Read(ref reads);
            ((Tereditor.UI.Panels.SettingsView)settings).ShowSection("Code Intelligence");
            E2E.E2eWorkspace.Until(() => Volatile.Read(ref reads) > before, "The rebuilt section did not ask for the terms itself.");
            Require(status() == "Checking license terms…", "The rebuilt section must be checking, not show an answer of the old one.");
            gate.SetResult();
            StartsWith(status, "Terms accepted on 2026-11-07", "The rebuilt section stayed unresolved.");
        }, app =>
        {
            app.Host.TermsReply = _ => result(true);
            app.Host.TermsBefore = async (_, token) => { Interlocked.Increment(ref reads); await gate.Task.WaitAsync(token); };
        });
        Console.WriteLine("PASS Settings terms outcomes: accepted, not accepted, failed read with Retry, host deadline, page deadline, section rebuilt mid-read");
    }

    private static string Licensing(JsonObject state) => Plugin(IntelliJLicensing.Project(state));

    private static string Plugin(JsonObject view)
    {
        view["busy"] = "";
        return view.ToJsonString();
    }
}