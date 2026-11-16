using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.Bedrock.UI;

/// <summary>The Amazon Bedrock plugin's UI half: one component in the Cloud platforms group of Settings › Plugins › Model providers.</summary>
public sealed class BedrockUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SlotSection(new(ProvidersSlots.Hyperscalers, _ => new BedrockView(context)));
        return null;
    }
}