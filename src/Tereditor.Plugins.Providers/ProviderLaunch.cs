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

    /// <summary>The prefix of the provider id Codex sees. Codex keeps its built-in ids (<c>openai</c>) for itself, so this one is always ours.</summary>
    public const string CodexIdPrefix = "tereditor-";

    /// <summary>
    /// The Codex launch contribution for the model source <paramref name="report"/> records, or <see langword="null"/> when there is
    /// no report, the launcher is not Codex or no source is chosen: then the launch is exactly the agent's own. A chosen source that
    /// cannot be used blocks the launch with a reason, so it never starts on another account.
    /// </summary>
    /// <param name="launcherId">The launcher asking.</param>
    /// <param name="report">The latest merged list and sources.</param>
    public static AgentLaunchContribution? For(string launcherId, ModelProvidersReport? report)
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
