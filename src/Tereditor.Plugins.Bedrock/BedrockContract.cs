using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.Bedrock;

/// <summary>The non-secret settings: where the platform's models are. The credentials stay with the platform's own tools.</summary>
public sealed record BedrockSettings
{
    public string Region { get; set; } = "";
    public string Profile { get; set; } = "";
}

/// <summary>What the Amazon Bedrock plugin says: the provider it adds to Model providers and the launch variables that come with it.</summary>
public static partial class BedrockPlatform
{
    /// <summary>The provider id, which is also the id of its model source.</summary>
    public const string ProviderId = "bedrock";

    public const string Name = "Amazon Bedrock";

    public const string Note = "Uses your own AWS credentials: a profile, single sign-on or the variables the AWS tools read. tereditor stores no AWS secret. Sign in with the AWS tools first, for example aws sso login.";

    [GeneratedRegex("^[a-z]{2}(-[a-z0-9]+)+-[0-9]$")]
    private static partial Regex RegionPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ProfilePattern();

    /// <summary>A plain sentence saying what is missing or wrong in <paramref name="settings"/>; null when the provider can be offered.</summary>
    /// <param name="settings">The settings.</param>
    public static string? Problem(BedrockSettings settings)
    {
        if (!RegionPattern().IsMatch(settings.Region)) return "Region must be an AWS Region such as us-east-1.";
        if (!ProfilePattern().IsMatch(settings.Profile) && settings.Profile.Length > 0) return "Profile must be a profile name of letters, digits, dots, dashes and underscores.";
        return null;
    }

    /// <summary>The provider to contribute for <paramref name="settings"/>, or null while they are not complete: an unconfigured platform is never offered.</summary>
    /// <param name="settings">The settings.</param>
    public static ProviderDefinition? Definition(BedrockSettings settings)
    {
        if (Problem(settings) is not null) return null;
        var env = new Dictionary<string, string>
        {
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["AWS_REGION"] = settings.Region
        };
        if (settings.Profile.Length > 0) env["AWS_PROFILE"] = settings.Profile;
        return new(ProviderId, Name, ProviderGroup.Hyperscalers, [new(ModelApiKinds.Bedrock, $"https://bedrock-runtime.{settings.Region}.amazonaws.com", [ModelAuthKind.Platform])])
        {
            NeedsKey = false,
            Note = Note,
            Environment = env
        };
    }
}

public static class BedrockManifest
{
    public const string Id = "bedrock";

    public const string Icon = "asset:bedrock.svg";

    public static readonly PluginManifest Manifest = new(Id, "Amazon Bedrock", Icon, "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.ModelProviders,
        DependsOn = [new(ProvidersManifest.Id, ProvidersContract.WireVersion)],
        Description = "Adds Amazon Bedrock to Model providers for Claude Code, with your own AWS credentials.",
        Support = PluginSupportLevels.Early,
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        UnavailableOn = [HostKind.PureWeb],
        Assets = "assets",
        Host = "Tereditor.Plugins.Bedrock.Host.dll",
        Ui = "Tereditor.Plugins.Bedrock.UI.dll"
    };

    public static readonly PluginContract Contract = PluginContract.Create<BedrockSettings>(Id, 1, [], [], BedrockJson.Default);
}

[JsonSerializable(typeof(BedrockSettings))]
internal sealed partial class BedrockJson : JsonSerializerContext;