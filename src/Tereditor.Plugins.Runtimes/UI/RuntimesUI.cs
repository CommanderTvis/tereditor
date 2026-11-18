using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Runtimes.UI;

/// <summary>The UI half: Settings › Runtimes for tooling.</summary>
public sealed class RuntimesUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Runtimes", RuntimesPlugin.Manifest.Icon, () => new RuntimesSettingsView(context)));
        var prompts = new RuntimeConsentPrompts(context);
        context.Subscribe(RuntimesContract.ConsentChanged, prompts.Apply, new RuntimesStatusParams());
        return prompts.Dispose;
    }
}