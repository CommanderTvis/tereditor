using System.Text;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers.Host;

/// <summary>
/// The models file of the agents that read one: Pi's <c>models.json</c> in tereditor's own agent directory and omp's <c>models.yml</c> in
/// its <c>tereditor</c> profile. A file names the chosen provider's endpoint and gives the key as a <c>!command</c> that runs the
/// <c>provider-key</c> helper, so no key is ever written here. Pi's <c>auth.json</c> is never created or changed.
/// </summary>
internal sealed class AgentModelFiles(ProviderKeyStore keys, string stateRoot, string? ompHome = null)
{
    private const string Marker = "# Written by tereditor. Changes are overwritten.";

    public const string Pi = "pi";
    public const string Omp = "omp";

    // Providers an agent already knows by id, from the provider tables of Pi (docs/providers.md) and omp (https://omp.sh/docs/providers): the file only
    // redirects the endpoint and gives the key. Any other provider needs a list of models, which tereditor cannot write yet.
    private static readonly string[] PiKnown = ["anthropic", "openai", "openrouter", "deepseek", "mistral", "groq", "xai", "cerebras", "fireworks"];
    private static readonly string[] OmpKnown = [.. PiKnown, "together"];

    private static bool Known(string agentId, string id) => (agentId == Pi ? PiKnown : OmpKnown).Contains(id);

    public static bool Handles(string agentId) => agentId is Pi or Omp;

    /// <summary>The agent directory Pi runs with once a model source is chosen; it holds no other file than <c>models.json</c>.</summary>
    public string PiAgentDirectory => Path.Combine(stateRoot, "providers", "pi", "agent");

    public string OmpAgentDirectory => Path.Combine(
        ompHome ?? (Environment.GetEnvironmentVariable("TEREDITOR_OMP_HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        ".omp", "profiles", "tereditor", "agent");

    public string PathOf(string agentId) => agentId == Pi ? Path.Combine(PiAgentDirectory, "models.json") : Path.Combine(OmpAgentDirectory, "models.yml");

    /// <summary>
    /// Writes the file for <paramref name="provider"/>. A provider the agent knows by id only gets its endpoint and key. Any other becomes a generic
    /// entry of the agent's own adapter for that API, with a model list: the provider's models, else <paramref name="model"/>, else <c>default</c>.
    /// </summary>
    public void Write(string agentId, ProviderDefinition provider, string? model = null)
    {
        var known = Known(agentId, provider.Id);
        var api = Pick(provider, known) ?? throw new ArgumentException("This agent cannot use that provider.");
        ProviderKeyHelper.Ensure(keys.KeysDirectory, keys.BinDirectory);
        var models = known ? [] : provider.Models.Count > 0 ? provider.Models : [model is { Length: > 0 } ? model : "default"];
        var text = agentId == Pi ? PiJson(provider, api, KeyCommand(provider.Id), known, models) : OmpYaml(provider, api, KeyCommand(provider.Id), known, models);
        PrivateFile.Write(PathOf(agentId), text);
    }

    public void Remove(string agentId)
    {
        var path = PathOf(agentId);
        // omp's file lives in a profile the user may also edit: only a file this plugin wrote is removed.
        if (File.Exists(path) && (agentId == Pi || File.ReadLines(path).FirstOrDefault() == Marker)) File.Delete(path);
    }

    /// <summary>The words a shell runs to print the key of <paramref name="id"/>: the helper and the id, quoted for the host's shell.</summary>
    public string KeyCommand(string id) => string.Join(' ', ProviderKeyHelper.Words(keys.BinDirectory, id).Select(Quote));

    private static string Quote(string word) =>
        word.Any(char.IsControl) ? throw new ArgumentException("A path cannot hold control characters.")
        : OperatingSystem.IsWindows() ? "\"" + word + "\"" : "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static IReadOnlyList<string> PreferredKinds(string id, bool known) =>
        known ? id == "anthropic" ? [ModelApiKinds.AnthropicMessages] : [ModelApiKinds.OpenAiChat, ModelApiKinds.OpenAiResponses, ModelApiKinds.AnthropicMessages]
        : [ModelApiKinds.OpenAiChat, ModelApiKinds.OpenAiResponses, ModelApiKinds.AnthropicMessages];

    private static ProviderApi? Pick(ProviderDefinition provider, bool known) =>
        PreferredKinds(provider.Id, known).Select(kind => provider.Apis.FirstOrDefault(api => api.Kind == kind)).FirstOrDefault(api => api is not null);

    private static string ApiName(string kind) => kind switch
    {
        ModelApiKinds.AnthropicMessages => "anthropic-messages",
        ModelApiKinds.OpenAiResponses => "openai-responses",
        _ => "openai-completions"
    };

    internal static string PiJson(ProviderDefinition provider, ProviderApi api, string keyCommand, bool known, IReadOnlyList<string> models)
    {
        var text = new StringBuilder("{\n  \"providers\": {\n    ").Append(Json(provider.Id)).Append(": {\n      \"baseUrl\": ").Append(Json(api.BaseUrl));
        if (!known) text.Append(",\n      \"api\": ").Append(Json(ApiName(api.Kind)));
        text.Append(",\n      \"apiKey\": ").Append(Json(provider.NeedsKey ? "!" + keyCommand : "none"));
        if (models.Count > 0) text.Append(",\n      \"models\": [").Append(string.Join(", ", models.Select(id => "{ \"id\": " + Json(id) + " }"))).Append(']');
        return text.Append("\n    }\n  }\n}\n").ToString();
    }

    internal static string OmpYaml(ProviderDefinition provider, ProviderApi api, string keyCommand, bool known, IReadOnlyList<string> models)
    {
        var text = new StringBuilder(Marker + "\nproviders:\n  ").Append(Json(provider.Id)).Append(":\n    baseUrl: ").Append(Json(api.BaseUrl)).Append('\n');
        if (!known) text.Append("    api: ").Append(ApiName(api.Kind)).Append('\n');
        text.Append(provider.NeedsKey ? "    apiKey: " + Json("!" + keyCommand) : "    auth: none").Append('\n');
        if (models.Count > 0) text.Append("    models:\n").AppendJoin("", models.Select(id => "      - id: " + Json(id) + "\n"));
        return text.ToString();
    }

    // A JSON string is also a YAML double-quoted scalar. Ids and URLs are validated; a path may hold quotes or backslashes.
    private static string Json(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
            text.Append(c switch { '"' => "\\\"", '\\' => "\\\\", _ when c < ' ' => "\\u" + ((int)c).ToString("x4"), _ => c.ToString() });
        return text.Append('"').ToString();
    }
}