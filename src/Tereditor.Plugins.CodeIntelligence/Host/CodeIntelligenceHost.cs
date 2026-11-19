using System.Text.Json;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.CodeIntelligence.Host;

/// <summary>
/// The host half of the Code Intelligence plugin: the page's calls over the host's language service and settings, and the
/// settings state every client shows. The language service, the lifecycle and the installation stay in the host; this only
/// maps them to the contract's records, so no language behaviour is implemented here.
/// </summary>
public sealed class CodeIntelligenceHost : PluginHostModule
{
    private long revision;

    public override PluginContract Contract => CodeIntelligenceContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.Method(CodeIntelligenceContract.Catalog, async (request, _, token) => Catalog(await Call(context, "catalog", request, token)));
        context.Method(CodeIntelligenceContract.Resolution, async (request, _, token) => Resolution(await Call(context, "resolution", request, token)));
        context.Method(CodeIntelligenceContract.Provision, async (request, _, token) =>
        {
            var result = await Call(context, "provision", request, token);
            var state = Text(result, "state");
            return new LanguageProvisionResult(state == "ready" ? "ready" : "failed", Text(result, "detail"));
        });
        context.Method(CodeIntelligenceContract.ConfigurationSchema, async (request, _, token) =>
        {
            var result = await Call(context, "configurationSchema", request, token);
            return new LanguageSchemaResult(Text(result, "state"), Text(result, "configuration"));
        });
        context.Method(CodeIntelligenceContract.Running, async (request, _, token) => Running(await Call(context, "running", request, token)));
        context.Method(CodeIntelligenceContract.Terms, async (request, _, token) =>
        {
            using var document = JsonDocument.Parse(await context.LanguageTermsAsync(request.Workspace, request.Server, request.Accept, request.ArtifactId, request.EulaSha256, request.Answers, token));
            return Terms(document.RootElement);
        });
        context.Method(CodeIntelligenceContract.SetServerOptions, async (request, _, token) =>
        {
            await context.SetLanguageSettingAsync("language-server:" + request.Server, ServerOptions(request), token);
            return await Settings(context);
        });
        context.Method(CodeIntelligenceContract.SetLimits, async (request, _, token) =>
        {
            await context.SetLanguageSettingAsync("language-limits", request.Value, token);
            return await Settings(context);
        });
        context.Method(CodeIntelligenceContract.SetEnabled, async (request, _, token) =>
        {
            await context.SetLanguageSettingAsync("lsp-enabled", request.Value, token);
            return await Settings(context);
        });
        context.Method(CodeIntelligenceContract.SetProvider, async (request, _, token) =>
        {
            await context.SetLanguageSettingAsync("java-kotlin-provider", request.Value, token);
            return await Settings(context);
        });
        context.Method(CodeIntelligenceContract.ReadSettings, (_, _, _) => Settings(context));
        // The host owns the state; this publishes what it reads again, never a copy it kept.
        context.OnLanguageChanged(catalog => _ = Publish(context, catalog));
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    private async Task Publish(IPluginHostContext context, bool catalog)
    {
        try
        {
            context.Publish(CodeIntelligenceContract.Settings, await Settings(context));
            if (catalog) context.Publish(CodeIntelligenceContract.CatalogChanged, new CodeIntelligenceCatalogChange(Interlocked.Increment(ref revision)));
        }
        catch (Exception error)
        {
            context.Log.Warn("The language settings could not be published: " + error.Message);
        }
    }

    internal static async ValueTask<CodeIntelligenceSettingsState> Settings(IPluginHostContext context)
    {
        var settings = await context.LanguageSettingsAsync();
        return new(settings.Enabled, settings.LanguageServers, settings.LanguageTools, settings.Limits, settings.JavaKotlinProvider);
    }

    private static async ValueTask<JsonElement> Call(IPluginHostContext context, string operation, LanguageServerParams request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await context.LanguageCallAsync(operation, request.Workspace, request.Server, token));
        return document.RootElement.Clone();
    }

    /// <summary>The options JSON the host saves a server's launcher choices from.</summary>
    internal static string ServerOptions(LanguageServerOptionsParams options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("allowPath", options.AllowPath);
            writer.WriteString("command", options.Command);
            if (options.Arguments is { } arguments)
            {
                writer.WriteStartArray("arguments");
                foreach (var argument in arguments) writer.WriteStringValue(argument);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static LanguageCatalogState Catalog(JsonElement result) =>
        new([.. Items(result, "servers").Select(server => new LanguageServerRow(Text(server, "id"), Text(server, "name"), Text(server, "version"), Text(server, "detail"), Flag(server, "hasTerms"),
            server.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array
                ? [.. dependencies.EnumerateArray().Select(dependency => new LanguageDependencyRow(Text(dependency, "executable"), Text(dependency, "name"), Text(dependency, "state"), Text(dependency, "path"),
                    Text(dependency, "source"), Text(dependency, "installHint"), Text(dependency, "problem"), Flag(dependency, "override"), !dependency.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.False))]
                : null))], Text(result, "detail"));

    internal static LanguageResolution Resolution(JsonElement result)
    {
        using var facts = JsonDocument.Parse(Text(result, "configuration") is { Length: > 0 } text ? text : "{}");
        var root = facts.RootElement;
        return new(Text(result, "state"), Text(result, "detail"), Text(root, "source"), Text(root, "path"), Text(root, "version"), Text(root, "warning"), Text(root, "pinning"), Flag(root, "pathCapable"));
    }

    internal static LanguageRunningState Running(JsonElement result)
    {
        using var facts = JsonDocument.Parse(Text(result, "configuration") is { Length: > 0 } text ? text : "{}");
        var root = facts.RootElement;
        var caps = new Dictionary<string, int>();
        if (root.TryGetProperty("defaultCaps", out var defaults) && defaults.ValueKind == JsonValueKind.Object)
            foreach (var cap in defaults.EnumerateObject()) caps[cap.Name] = cap.Value.GetInt32();
        return new([.. Items(result, "servers").Select(server => new LanguageRunningRow(Text(server, "id"), Text(server, "workspace"), Number(server, "memoryBytes"), Number(server, "idleSeconds")))],
            Number(root, "budgetBytes"), Number(root, "physicalBytes"), Flag(root, "automaticBudget"), Flag(root, "pressure"), caps);
    }

    internal static LanguageTermsState Terms(JsonElement result)
    {
        var answers = new Dictionary<string, string>();
        if (result.TryGetProperty("answers", out var recorded) && recorded.ValueKind == JsonValueKind.Object)
            foreach (var answer in recorded.EnumerateObject()) answers[answer.Name] = answer.Value.GetString() ?? "";
        return new(Text(result, "state"), Text(result, "detail"), Text(result, "version"), Text(result, "artifactId"), Text(result, "eulaSha256"), Text(result, "text"), Flag(result, "accepted"), Text(result, "acceptedAtUtc"),
            [.. Items(result, "questions").Select(question => new LanguageTermsQuestionRow(Text(question, "id"), Text(question, "prompt"), Strings(question, "values"), Strings(question, "labels")))], answers);
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static string[] Strings(JsonElement element, string name) => [.. Items(element, name).Select(item => item.GetString() ?? "")];

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}
