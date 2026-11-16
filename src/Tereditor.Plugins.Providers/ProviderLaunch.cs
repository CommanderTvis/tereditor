using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

/// <summary>
/// What the chosen model source adds to an agent's launch. Only Codex has a seam so far. A key is never part of it: the launch
/// names the <c>provider-key</c> helper, which Codex runs itself to read the key.
/// </summary>
public static class ProviderLaunch
{
    /// <summary>The launcher id and agent id of Codex.</summary>
    public const string Codex = "codex";

    /// <summary>The launcher id Claude Code asks with, and the agent id its model source is recorded under.</summary>
    public const string Claude = "claude";

    public const string ClaudeAgent = "claude-code";

    /// <summary>The prefix of the provider id Codex sees. Codex keeps its built-in ids (<c>openai</c>) for itself, so this one is always ours.</summary>
    public const string CodexIdPrefix = "tereditor-";

    /// <summary>
    /// The Codex launch contribution for the model source <paramref name="report"/> records, or <see langword="null"/> when there is
    /// no report, the launcher is not Codex or no source is chosen: then the launch is exactly the agent's own. A chosen source that
    /// cannot be used blocks the launch with a reason, so it never starts on another account.
    /// </summary>
    /// <param name="launcherId">The launcher asking.</param>
    /// <param name="report">The latest merged list and sources.</param>
    /// <param name="windows">Whether the launch line is read by a Windows shell, which gets no variables; null reads this host's system.</param>
    public static AgentLaunchContribution? For(string launcherId, ModelProvidersReport? report, bool? windows = null)
    {
        if (launcherId == Claude) return ForClaude(report, windows ?? OperatingSystem.IsWindows());
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

    /// <summary>
    /// Claude Code runs on a cloud platform through variables of its own (<c>CLAUDE_CODE_USE_BEDROCK</c>, <c>CLAUDE_CODE_USE_VERTEX</c>). Only a source whose provider takes
    /// the platform's credentials has this seam; any other source stays recorded only, and the launch stays the agent's own. A launch line a Windows
    /// shell reads gets no variables, so it is blocked rather than started on another account.
    /// </summary>
    private static AgentLaunchContribution? ForClaude(ModelProvidersReport? report, bool windows)
    {
        if (report?.Sources.FirstOrDefault(choice => choice.AgentId == ClaudeAgent) is not { } source
            || report.Providers.FirstOrDefault(row => row.Provider.Id == source.ProviderId) is not { Ready: true } row
            || !row.Provider.Apis.Any(api => api.Kind is ModelApiKinds.Bedrock or ModelApiKinds.Vertex && api.Auth.Contains(ModelAuthKind.Platform))) return null;
        if (windows) return Blocked("Claude Code runs on a cloud platform only in a POSIX shell for now. Choose its own provider.");
        return row.Provider.Environment.Count == 0 ? Blocked("The chosen cloud platform has no settings yet. Fill them in Settings › Plugins › Model providers.")
            : new() { Environment = row.Provider.Environment };
    }

    private static AgentLaunchContribution Blocked(string reason) => new() { BlockedReason = reason };
}