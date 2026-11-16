using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

/// <summary>
/// What the chosen model source adds to an agent's launch. Codex and Claude Code have a seam here (Pi and omp have theirs in the host half).
/// A key is never part of it: Codex's launch names the <c>provider-key</c> helper, and Claude Code's settings file does.
/// </summary>
public static class ProviderLaunch
{
    /// <summary>The launcher id and agent id of Codex.</summary>
    public const string Codex = "codex";

    /// <summary>The launcher id Claude Code's contributions are asked for.</summary>
    public const string ClaudeLauncher = "claude";

    /// <summary>The agent id of Claude Code, which its model source is recorded under.</summary>
    public const string ClaudeAgent = "claude-code";

    /// <summary>
    /// The generated settings file Claude Code starts with: <c>providers/claude-code/settings.json</c> next to the helper's <c>providers/bin</c>.
    /// Both halves derive it from the helper's path, so the launch line never reads a file or needs a second report field.
    /// </summary>
    /// <param name="helper">The path of the <c>provider-key</c> helper.</param>
    public static string? ClaudeSettings(string? helper) =>
        Path.GetDirectoryName(Path.GetDirectoryName(helper)) is { Length: > 0 } providers ? Path.Combine(providers, ClaudeAgent, "settings.json") : null;

    /// <summary>The Anthropic-compatible API Claude Code can be started with, or null: Claude Code takes only an API key.</summary>
    /// <param name="provider">The provider.</param>
    public static ProviderApi? ClaudeApi(ProviderDefinition provider) =>
        provider.Apis.FirstOrDefault(api => api.Kind == ModelApiKinds.AnthropicMessages && api.Auth.Contains(ModelAuthKind.ApiKey));

    /// <summary>The prefix of the provider id Codex sees. Codex keeps its built-in ids (<c>openai</c>) for itself, so this one is always ours.</summary>
    public const string CodexIdPrefix = "tereditor-";

    /// <summary>
    /// The Codex launch contribution for the model source <paramref name="report"/> records, or <see langword="null"/> when there is
    /// no report, the launcher is not Codex or no source is chosen: then the launch is exactly the agent's own. A chosen source that
    /// cannot be used blocks the launch with a reason, so it never starts on another account.
    /// </summary>
    /// <param name="launcherId">The launcher asking.</param>
    /// <param name="report">The latest merged list and sources.</param>
    public static AgentLaunchContribution? For(string launcherId, ModelProvidersReport? report) =>
        launcherId == ClaudeLauncher ? ForClaude(report) : ForCodex(launcherId, report);

    // Claude Code reads the generated settings file (apiKeyHelper and the endpoint); it is the only argument, so a --settings in the
    // user's own command blocks the launch (two may not merge); their other arguments and variables stay as written.
    private static AgentLaunchContribution? ForClaude(ModelProvidersReport? report)
    {
        if (report?.Sources.FirstOrDefault(choice => choice.AgentId == ClaudeAgent) is not { } source) return null;
        if (report.Providers.FirstOrDefault(row => row.Provider.Id == source.ProviderId) is not { Ready: true } row) return Blocked("The chosen model source is not ready. Choose another in Settings › Plugins › Model providers.");
        if (ClaudeApi(row.Provider) is null) return Blocked("The chosen model source has no Anthropic API for Claude Code.");
        if (ClaudeSettings(report.Helper) is not { } path) return Blocked("The key helper is missing. Save the key again in Settings › Plugins › Model providers.");
        // A path the launch line cannot quote safely would drop the whole contribution and start Claude Code on its own account.
        return path.Any(char.IsControl) || OperatingSystem.IsWindows() && path.IndexOfAny(['"', '%', '$', '`', '^', '&', '|', '<', '>']) >= 0
            ? Blocked("The settings file path has a character Claude Code cannot take.")
            : new()
            {
                Arguments = ["--settings", path],
                ConflictingArguments = ["--settings"],
                ConflictReason = "The Claude command has its own --settings, so the model source cannot apply. Remove it or clear the source."
            };
    }

    private static AgentLaunchContribution? ForCodex(string launcherId, ModelProvidersReport? report)
    {
        if (launcherId != Codex || report?.Sources.FirstOrDefault(choice => choice.AgentId == Codex) is not { } source) return null;
        if (report.Providers.FirstOrDefault(row => row.Provider.Id == source.ProviderId) is not { Ready: true } row) return Blocked("The chosen model source is not ready. Choose another in Settings › Plugins › Model providers.");
        var provider = row.Provider;
        if (provider.Apis.FirstOrDefault(api => api.Kind == ModelApiKinds.OpenAiResponses && api.Auth.Contains(ModelAuthKind.ApiKey)) is not { } responses)
            return Blocked("The chosen model source has no OpenAI Responses API for Codex.");
        var id = CodexIdPrefix + new string([.. provider.Id.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-')]);
        var name = new string([.. provider.Name.Where(c => c != '\'' && !char.IsControl(c))]);
        var words = new List<string>();
        void Set(string key, string value) => words.AddRange(["-c", key + "=" + value]);
        // TOML literal strings, so no word needs a double quote and Windows quoting keeps them.
        Set("model_provider", "'" + id + "'");
        Set("model_providers." + id + ".name", "'" + name + "'");
        Set("model_providers." + id + ".base_url", "'" + responses.BaseUrl + "'");
        Set("model_providers." + id + ".wire_api", "'responses'");
        if (provider.NeedsKey)
        {
            if (report.Helper is not { Length: > 0 } helper) return Blocked("The key helper is missing. Save the key again in Settings › Plugins › Model providers.");
            Set("model_providers." + id + ".auth.command", "'" + helper + "'");
            Set("model_providers." + id + ".auth.args", "['" + provider.Id + "']");
        }
        return words.Any(word => word.Any(char.IsControl)) || responses.BaseUrl.Contains('\'') || report.Helper?.Contains('\'') == true
            ? Blocked("The chosen model source has a name or address Codex cannot take.")
            : new() { Arguments = words };
    }

    private static AgentLaunchContribution Blocked(string reason) => new() { BlockedReason = reason };
}