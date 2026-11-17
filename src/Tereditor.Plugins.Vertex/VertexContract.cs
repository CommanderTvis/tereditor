using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.Vertex;

/// <summary>The non-secret settings: where the platform's models are. The credentials stay with the platform's own tools.</summary>
public sealed record VertexSettings
{
    public string Project { get; set; } = "";
    public string Region { get; set; } = "global";
}

/// <summary>What the Google Vertex AI plugin says: the provider it adds to Model providers and the launch variables that come with it.</summary>
public static partial class VertexPlatform
{
    /// <summary>The provider id, which is also the id of its model source.</summary>
    public const string ProviderId = "vertex";

    public const string Name = "Google Vertex AI";

    public const string Note = "Uses your own Google Cloud credentials: application default credentials. tereditor stores no Google secret. Sign in with gcloud auth application-default login first.";

    [GeneratedRegex("^[a-z][a-z0-9-]{4,28}[a-z0-9]$")]
    private static partial Regex ProjectPattern();

    [GeneratedRegex("^[a-z0-9-]{2,32}$")]
    private static partial Regex RegionPattern();

    /// <summary>A plain sentence saying what is missing or wrong in <paramref name="settings"/>; null when the provider can be offered.</summary>
    /// <param name="settings">The settings.</param>
    public static string? Problem(VertexSettings settings)
    {
        if (!ProjectPattern().IsMatch(settings.Project)) return "Project must be a Google Cloud project id such as my-project-123.";
        if (!RegionPattern().IsMatch(settings.Region)) return "Region must be a region such as global or us-east5.";
        return null;
    }

    /// <summary>The provider to contribute for <paramref name="settings"/>, or null while they are not complete: an unconfigured platform is never offered.</summary>
    /// <param name="settings">The settings.</param>
    public static ProviderDefinition? Definition(VertexSettings settings)
    {
        if (Problem(settings) is not null) return null;
        var env = new Dictionary<string, string>
        {
            ["CLAUDE_CODE_USE_VERTEX"] = "1",
            ["CLOUD_ML_REGION"] = settings.Region,
            ["ANTHROPIC_VERTEX_PROJECT_ID"] = settings.Project
        };

        return new(ProviderId, Name, ProviderGroup.Hyperscalers, [new(ModelApiKinds.Vertex, settings.Region == "global" ? "https://aiplatform.googleapis.com" : $"https://{settings.Region}-aiplatform.googleapis.com", [ModelAuthKind.Platform])])
        {
            NeedsKey = false,
            Note = Note,
            Environment = env
        };
    }
}

public static class VertexManifest
{
    public const string Id = "vertex";

    public const string Icon = "asset:vertex.svg";

    public static readonly PluginManifest Manifest = new(Id, "Google Vertex AI", Icon, "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.ModelProviders,
        DependsOn = [new(ProvidersManifest.Id, ProvidersContract.WireVersion)],
        Description = "Adds Google Vertex AI to Model providers for Claude Code, with your own Google Cloud credentials.",
        Support = PluginSupportLevels.Early,
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        UnavailableOn = [HostKind.PureWeb],
        Assets = "assets",
        Host = "Tereditor.Plugins.Vertex.Host.dll",
        Ui = "Tereditor.Plugins.Vertex.UI.dll"
    };

    public static readonly PluginContract Contract = PluginContract.Create<VertexSettings>(Id, 1, [], [], VertexJson.Default);
}

[JsonSerializable(typeof(VertexSettings))]
internal sealed partial class VertexJson : JsonSerializerContext;