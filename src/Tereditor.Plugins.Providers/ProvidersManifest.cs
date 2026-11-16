using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

public static class ProvidersManifest
{
    public const string Id = "providers";
    public const string Icon = "asset:providers.svg";

    public static readonly PluginManifest Manifest = new(Id, "Model providers", Icon, "0.1.0", PluginApi.Generation, ProvidersContract.WireVersion)
    {
        Category = PluginCategories.ModelProviders,
        Description = "Keeps your API keys and custom providers. Offers them to the agents that can use them.",
        Support = PluginSupportLevels.Early,
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        Host = "Tereditor.Plugins.Providers.Host.dll",
        Ui = "Tereditor.Plugins.Providers.UI.dll",
        Assets = "assets"
    };
}