using System.Text.Json.Nodes;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core.Plugins;

/// <summary>The language settings and service calls the Code Intelligence page makes through its host half.</summary>
internal sealed partial class PluginHostContext
{
    private static readonly string[] SettingsOperations = ["catalog", "resolution", "provision", "configurationSchema", "running"];

    public ValueTask<HostLanguageSettings> LanguageSettingsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(LanguageSettingsOf(runtime.Seams.State.Current.Settings));

    internal static HostLanguageSettings LanguageSettingsOf(HostSettings settings) =>
        new(settings.LspEnabled, settings.LanguageServers, settings.LanguageTools, settings.LanguageLimits, settings.JavaKotlinProvider);

    public void OnLanguageChanged(Action<bool> handler) => Register(table => table.LanguageObservers = table.LanguageObservers.Add(handler));

    public async ValueTask SetLanguageSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key is not ("lsp-enabled" or "java-kotlin-provider" or "language-limits") && !(key.StartsWith("language-server:", StringComparison.Ordinal) && key.Length > 16))
            throw new ArgumentException($"plugin {Id} may not write the host setting \"{key}\"", nameof(key));
        if (!Live) return;
        await runtime.Seams.State.ChangeAsync([HostStateChange.Setting(key, value)], cancellationToken);
    }

    public async ValueTask<string> LanguageCallAsync(string operation, string workspace, string server = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Array.IndexOf(SettingsOperations, operation) < 0) throw new ArgumentException($"plugin {Id} may not run the language operation \"{operation}\"", nameof(operation));
        if (!Live) return "{}";
        var result = await runtime.Seams.State.Languages.ExecuteAsync(new LanguageRequest(workspace, Operation: operation, Server: server), cancellationToken);
        var servers = new JsonArray();
        foreach (var state in result.Servers)
            servers.Add((JsonNode?)new JsonObject
            {
                ["id"] = state.Id,
                ["name"] = state.Presentation?.Name ?? state.Id,
                ["state"] = state.State,
                ["detail"] = state.Detail,
                ["version"] = state.Version,
                ["hasTerms"] = state.Presentation?.HasTerms == true,
                ["workspace"] = state.Workspace,
                ["memoryBytes"] = state.MemoryBytes,
                ["idleSeconds"] = state.IdleSeconds,
                ["dependencies"] = state.Dependencies is null ? null : new JsonArray([.. state.Dependencies.Select(dependency => (JsonNode?)new JsonObject
                {
                    ["executable"] = dependency.Executable,
                    ["name"] = dependency.Name,
                    ["state"] = dependency.State,
                    ["path"] = dependency.Path,
                    ["source"] = dependency.Source,
                    ["installHint"] = dependency.InstallHint,
                    ["problem"] = dependency.Problem,
                    ["override"] = dependency.Override,
                    ["required"] = dependency.Required
                })])
            });
        return new JsonObject { ["state"] = result.State, ["detail"] = result.Detail, ["configuration"] = result.Configuration, ["servers"] = servers }.ToJsonString();
    }

    public async ValueTask<string> LanguageTermsAsync(string workspace, string server, bool accept = false, string artifactId = "", string eulaSha256 = "",
        IReadOnlyDictionary<string, string>? answers = null, CancellationToken cancellationToken = default)
    {
        if (!Live) return "{}";
        var request = new LanguageTermsRequest(workspace, server, accept, artifactId, eulaSha256) { Answers = answers ?? new Dictionary<string, string>() };
        var result = await runtime.Seams.State.Languages.TermsAsync(request, cancellationToken);
        return new JsonObject
        {
            ["state"] = result.State,
            ["detail"] = result.Detail,
            ["version"] = result.Version,
            ["artifactId"] = result.ArtifactId,
            ["eulaSha256"] = result.EulaSha256,
            ["text"] = result.Text,
            ["accepted"] = result.Accepted,
            ["acceptedAtUtc"] = result.AcceptedAtUtc,
            ["questions"] = new JsonArray([.. result.Questions.Select(question => (JsonNode?)new JsonObject
            {
                ["id"] = question.Id,
                ["prompt"] = question.Prompt,
                ["values"] = new JsonArray([.. question.Values.Select(value => (JsonNode?)value)]),
                ["labels"] = new JsonArray([.. question.Labels.Select(label => (JsonNode?)label)])
            })]),
            ["answers"] = new JsonObject(result.Answers.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)))
        }.ToJsonString();
    }
}