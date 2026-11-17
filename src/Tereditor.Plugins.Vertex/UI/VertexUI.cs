using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Providers;

namespace Tereditor.Plugins.Vertex.UI;

/// <summary>The Google Vertex AI plugin's UI half: one component in the provider list of Settings › Plugins › Model providers.</summary>
public sealed class VertexUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SlotSection(new(ProvidersSlots.Hyperscalers, _ => new VertexView(context)));
        return null;
    }
}