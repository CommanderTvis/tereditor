using Avalonia.Controls;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.CodeIntelligence;

namespace Tereditor.UI.Plugins;

/// <summary>
/// The UI half of the Code Intelligence plugin: its Settings section, listed under Plugins while the plugin is on. Settings builds
/// the page itself (it needs the window and host state), so the registration only decides that the section exists.
/// </summary>
internal sealed class CodeIntelligenceUI : PluginUIModule
{
    /// <summary>The section's key in Settings, <c>plugin:&lt;plugin&gt;:&lt;section&gt;</c>.</summary>
    internal const string SectionKey = "plugin:" + CodeIntelligenceContract.Id + ":" + CodeIntelligenceContract.Id;

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Code Intelligence", CodeIntelligenceContract.Manifest.Icon, () => new StackPanel()));
        return null;
    }
}