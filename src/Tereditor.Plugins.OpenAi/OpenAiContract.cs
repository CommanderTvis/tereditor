using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.OpenAi;

public sealed record OpenAiSettings;

/// <summary>What the OpenAI component says and runs. The key goes through Model providers; the sign-in is Codex's own.</summary>
public static class OpenAiProvider
{
    /// <summary>The provider row this plugin's component works on: the bundled OpenAI API, which speaks the Responses API Codex takes.</summary>
    public const string ProviderId = "openai";

    /// <summary>What the Sign in action runs in a terminal. tereditor runs no sign-in flow of its own and keeps no token.</summary>
    public const string SignInCommand = "codex login";

    /// <summary>OpenAI's sign-in policy: allowed, with a note for remote hosts. Policy only; nothing here starts a flow.</summary>
    public static ProviderOAuth Policy { get; } = ProviderOAuthPolicy.For(ProviderOAuthPolicy.OpenAi);

    /// <summary>The sentence under the sign-in: the policy, and who runs the sign-in.</summary>
    public static string SignInText => Policy.Allowed
        ? Policy.Note + " Codex keeps its own sign-in; tereditor runs it for you but never reads or stores its tokens."
        : Policy.ReasonIfNot ?? "";

    /// <summary>Whether the report holds a key for the OpenAI provider.</summary>
    public static bool KeyStored(ModelProvidersReport report) => report.Providers.Any(row => row.Provider.Id == ProviderId && row.Ready);
}

public static class OpenAiManifest
{
    public const string Id = "openai";

    public static readonly PluginManifest Manifest = new(Id, "OpenAI", "", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.ModelProviders,
        DependsOn = [new(ProvidersManifest.Id, ProvidersContract.WireVersion)],
        Description = "Adds OpenAI to Model providers: an API key for Codex, and a sign-in that runs Codex's own.",
        Support = PluginSupportLevels.Early,
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        Ui = "Tereditor.Plugins.OpenAi.UI.dll"
    };

    public static readonly PluginContract Contract = PluginContract.Create<OpenAiSettings>(Id, 1, [], [], OpenAiJson.Default);
}

[JsonSerializable(typeof(OpenAiSettings))]
internal sealed partial class OpenAiJson : JsonSerializerContext;
