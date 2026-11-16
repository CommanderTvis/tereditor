using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.OpenAi.UI;

/// <summary>The OpenAI plugin's UI half: one component in Settings › Plugins › Model providers, among the bundled routers and providers.</summary>
public sealed class OpenAiUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SlotSection(new(ProvidersSlots.Other, _ => new OpenAiView(context)));
        return null;
    }
}