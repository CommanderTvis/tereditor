using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

public static class ProvidersManifest
{
    public const string Id = "providers";
    public const string Icon = "asset:providers.svg";

    public static readonly PluginManifest Manifest = new(Id, "Model providers", Icon, "0.1.0", PluginApi.Generation, ProvidersContract.WireVersion)
    {
        Category = PluginCategories.ModelProviders,
        Description = "Keeps the API keys of model providers and lets Pi and omp use them without a key in any launch line.",
        Support = PluginSupportLevels.Early,
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        Host = "Tereditor.Plugins.Providers.Host.dll",
        Ui = "Tereditor.Plugins.Providers.UI.dll",
        Assets = "assets"
    };
}