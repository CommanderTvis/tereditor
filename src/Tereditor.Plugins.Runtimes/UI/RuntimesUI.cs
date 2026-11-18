using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Runtimes.UI;

/// <summary>The UI half: Settings › Runtimes for tooling.</summary>
public sealed class RuntimesUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Runtimes", RuntimesPlugin.Manifest.Icon, () => new RuntimesSettingsView(context)));
        return null;
    }
}