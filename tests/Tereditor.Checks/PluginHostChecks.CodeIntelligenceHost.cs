using System.Text.Json;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;
using Tereditor.Host.Core.Plugins;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.CodeIntelligence;
using Tereditor.Plugins.CodeIntelligence.Host;

namespace Tereditor.Checks;

/// <summary>The Code Intelligence host half: each method answers what the language service answers, field by field, and each write saves what the page saved before.</summary>
internal static partial class PluginHostChecks
{
    private static async Task CodeIntelligenceHostMethods(string root)
    {
        var directory = Path.Combine(root, "code-intelligence-host");
        Directory.CreateDirectory(directory);
        IPluginHostContext? probe = null;
        var probeModule = new Module(Contract("language-probe", Say, Other), context => { probe = context; return ValueTask.FromResult<PluginDisposer?>(null); });
        await using var composed = await Compose(directory, [.. BuiltinPlugins.LanguageServerEntries, .. BuiltinPlugins.All.Where(entry => entry.Manifest.Id == "runtimes"), (Manifest("language-probe", enabledByDefault: true), probeModule)]);
        await composed.Runtime.Schedule();
        Require(probe is not null, "The probe plugin activated.");
        var languages = composed.State.Languages;
        var settingsSeen = new List<CodeIntelligenceSettingsState>();
        var catalogSeen = new List<CodeIntelligenceCatalogChange>();
        using var settingsSubscription = composed.Runtime.SubscribeLocal(CodeIntelligenceContract.Id, CodeIntelligenceContract.Settings.Name, payload => { lock (settingsSeen) settingsSeen.Add(PluginJson.Convert<CodeIntelligenceSettingsState>(payload)); });
        using var catalogSubscription = composed.Runtime.SubscribeLocal(CodeIntelligenceContract.Id, CodeIntelligenceContract.CatalogChanged.Name, payload => { lock (catalogSeen) catalogSeen.Add(PluginJson.Convert<CodeIntelligenceCatalogChange>(payload)); });

        T Result<T>(object? value) => PluginJson.Convert<T>(value);
        async Task<T> Ask<T>(string method, object parameters) => Result<T>(await composed.Call(CodeIntelligenceContract.Id, method, JsonSerializer.SerializeToElement(parameters, new JsonSerializerOptions(JsonSerializerDefaults.Web))));

        // The catalog, field by field against the language service.
        var host = await languages.ExecuteAsync(new(directory, Operation: "catalog"));
        var catalog = await Ask<LanguageCatalogState>("catalog", new LanguageServerParams(directory));
        Require(host.Servers.Count > 0 && catalog.Servers.Count == host.Servers.Count && catalog.Detail == host.Detail, "The catalog lists the host's servers and its storage line.");
        for (var index = 0; index < host.Servers.Count; index++)
        {
            LanguageServerState state = host.Servers[index];
            var row = catalog.Servers[index];
            Require(row.Id == state.Id && row.Name == (state.Presentation?.Name ?? state.Id) && row.Version == state.Version && row.Detail == state.Detail && row.HasTerms == (state.Presentation?.HasTerms == true),
                "Catalog row " + state.Id + " carries the host's id, name, version, implementation line and terms flag.");
            Require((row.Dependencies is null) == (state.Dependencies is null) && (row.Dependencies is null || row.Dependencies.Count == state.Dependencies!.Count), "Catalog row " + state.Id + " carries the probed dependencies.");
            for (var tool = 0; row.Dependencies is not null && tool < row.Dependencies.Count; tool++)
            {
                var found = row.Dependencies[tool];
                var expected = state.Dependencies![tool];
                Require(found.Executable == expected.Executable && found.Name == expected.Name && found.State == expected.State && found.Path == expected.Path && found.Source == expected.Source &&
                    found.InstallHint == expected.InstallHint && found.Problem == expected.Problem && found.Override == expected.Override && found.Required == expected.Required,
                    "Dependency " + expected.Executable + " of " + state.Id + " carries every probed field.");
            }
        }

        // Resolution, schema and the running list.
        var server = host.Servers.First(candidate => candidate.Id == "css").Id;
        var hostResolution = await languages.ExecuteAsync(new(directory, Operation: "resolution", Server: server));
        var resolution = await Ask<LanguageResolution>("resolution", new LanguageServerParams(directory, server));
        using (var facts = JsonDocument.Parse(hostResolution.Configuration))
        {
            string Fact(string name) => facts.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            Require(resolution.State == hostResolution.State && resolution.Detail == hostResolution.Detail && resolution.Source == Fact("source") && resolution.Path == Fact("path") && resolution.Version == Fact("version") &&
                resolution.Warning == Fact("warning") && resolution.Pinning == Fact("pinning") && resolution.PathCapable == (facts.RootElement.TryGetProperty("pathCapable", out var capable) && capable.ValueKind == JsonValueKind.True),
                "Resolution carries the host's state, detail and every launcher fact.");
        }
        var hostSchema = await languages.ExecuteAsync(new(directory, Operation: "configurationSchema", Server: server));
        var schema = await Ask<LanguageSchemaResult>("configurationSchema", new LanguageServerParams(directory, server));
        Require(schema.State == hostSchema.State && schema.Schema == hostSchema.Configuration && schema.Schema.Length > 0, "The configuration schema is the host's, whole.");
        var hostRunning = await languages.ExecuteAsync(new(directory, Operation: "running"));
        var running = await Ask<LanguageRunningState>("running", new LanguageServerParams(directory));
        using (var summary = JsonDocument.Parse(hostRunning.Configuration))
        {
            var facts = summary.RootElement;
            Require(running.Servers.Count == hostRunning.Servers.Count && running.BudgetBytes == facts.GetProperty("budgetBytes").GetInt64() && running.PhysicalBytes == facts.GetProperty("physicalBytes").GetInt64() &&
                running.AutomaticBudget == facts.GetProperty("automaticBudget").GetBoolean() && running.Pressure == facts.GetProperty("pressure").GetBoolean() &&
                running.DefaultCaps.Count == facts.GetProperty("defaultCaps").EnumerateObject().Count() && facts.GetProperty("defaultCaps").EnumerateObject().All(cap => running.DefaultCaps[cap.Name] == cap.Value.GetInt32()),
                "The running state carries the host's budget, memory, pressure and default caps.");
        }

        // Terms: the mapper keeps every field.
        using (var terms = JsonDocument.Parse("""{"state":"requiresAcceptance","detail":"d","version":"1","artifactId":"a","eulaSha256":"h","text":"t","accepted":true,"acceptedAtUtc":"u","questions":[{"id":"q","prompt":"p","values":["x","y"],"labels":["X","Y"]}],"answers":{"q":"x"}}"""))
        {
            var mapped = CodeIntelligenceHost.Terms(terms.RootElement);
            Require(mapped.State == "requiresAcceptance" && mapped.Detail == "d" && mapped.Version == "1" && mapped.ArtifactId == "a" && mapped.EulaSha256 == "h" && mapped.Text == "t" && mapped.Accepted && mapped.AcceptedAtUtc == "u" &&
                mapped.Questions is [{ Id: "q", Prompt: "p" } question] && question.Values.SequenceEqual(["x", "y"]) && question.Labels.SequenceEqual(["X", "Y"]) && mapped.Answers["q"] == "x", "The terms state keeps every field the page shows.");
        }

        // Writes save the host settings the page saved before, and the state channel pushes the change.
        var before = composed.State.Current.Settings;
        await Ask<CodeIntelligenceSettingsState>("setEnabled", new CodeIntelligenceValue("false"));
        Require(!composed.State.Current.Settings.LspEnabled, "setEnabled false turns the host setting off.");
        await Until(() => Task.FromResult(settingsSeen.Count > 0 && settingsSeen[^1].Enabled == false), "the settings channel to push the switch");
        var enabled = await Ask<CodeIntelligenceSettingsState>("setEnabled", new CodeIntelligenceValue("true"));
        Require(enabled.Enabled && composed.State.Current.Settings.LspEnabled, "setEnabled true turns it on and answers the new state.");
        var limits = new Tereditor.Host.Core.Language.LanguageServerLimits(10, 4096, new Dictionary<string, int>()).Serialize();
        var limited = await Ask<CodeIntelligenceSettingsState>("setLimits", new CodeIntelligenceValue(limits));
        Require(limited.Limits == composed.State.Current.Settings.LanguageLimits && limited.Limits.Length > 0, "setLimits saves through the host and answers the saved limits.");
        var provider = await Ask<CodeIntelligenceSettingsState>("setProvider", new CodeIntelligenceValue(before.JavaKotlinProvider));
        Require(provider.JavaKotlinProvider == composed.State.Current.Settings.JavaKotlinProvider, "setProvider answers the saved provider.");

        var options = new LanguageServerOptionsParams(server, AllowPath: true, Command: "css-ls", Arguments: ["--stdio", "--x"]);
        await Ask<CodeIntelligenceSettingsState>("setServerOptions", options);
        var oldWay = new HostStateStore(Path.Combine(directory, "old-way"));
        await oldWay.ChangeAsync([HostStateChange.Setting("language-server:" + server,
            JsonSerializer.Serialize(new LanguageServerOptions(true, "css-ls", ["--stdio", "--x"]), HostJson.Web.Info<LanguageServerOptions>()))]);
        Require(composed.State.Current.Settings.LanguageServers == oldWay.Current.Settings.LanguageServers && composed.State.Current.Settings.LanguageServers.Length > 0,
            "setServerOptions saves the same launcher choices the page saved before: " + composed.State.Current.Settings.LanguageServers);
        var defaults = await Ask<CodeIntelligenceSettingsState>("setServerOptions", new LanguageServerOptionsParams(server));
        Require(defaults.LanguageServers == composed.State.Current.Settings.LanguageServers && !defaults.LanguageServers.Contains("css-ls"), "Default arguments (none given) are saved as defaults.");
        var read = await Ask<CodeIntelligenceSettingsState>("readSettings", new CodeIntelligenceSettingsParams());
        Require(read == defaults, "readSettings is the snapshot the channel pushes.");

        // A catalog change reaches the page: switching a language server plugin off bumps the revision.
        var revisions = catalogSeen.Count;
        await composed.Enable("lsp-css", false);
        await Until(() => Task.FromResult(catalogSeen.Count > revisions), "the catalog channel to announce the change");
        await composed.Enable("lsp-css");

        // Only the language settings and operations a plugin's page needs are open to it.
        foreach (var key in new[] { "editor-font-family", "notifications", "language-tool:css", "language-server:" })
            await Throws<ArgumentException>(() => probe!.SetLanguageSettingAsync(key, "x").AsTask(), "The key " + key + " is refused.");
        foreach (var operation in new[] { "hover", "rawRequest", "restart", "close" })
            await Throws<ArgumentException>(() => probe!.LanguageCallAsync(operation, directory, server).AsTask(), "The language operation " + operation + " is refused.");
        Console.WriteLine("PASS code intelligence host half: catalog, resolution, schema and running match the language service field by field, terms keep every field, writes save the keys the page saved and the settings and catalog channels push every change, other keys and operations are refused");
    }

    private static async Task Throws<TException>(Func<Task> call, string message) where TException : Exception
    {
        try { await call(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }
}