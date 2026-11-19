using Avalonia.Controls;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.CodeIntelligence.UI;

/// <summary>The UI half of the Code Intelligence plugin: its Settings section, listed under Plugins while the plugin is on.</summary>
public sealed class CodeIntelligenceUI : PluginUIModule
{
    /// <summary>The section's key in Settings, <c>plugin:&lt;plugin&gt;:&lt;section&gt;</c>.</summary>
    public const string SectionKey = "plugin:" + CodeIntelligenceContract.Id + ":" + CodeIntelligenceContract.Id;

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Code Intelligence", CodeIntelligenceContract.Manifest.Icon, () => new StackPanel())
        {
            CreateForWorkspace = workspace => new CodeIntelligencePage(context, workspace)
        });
        return null;
    }
}