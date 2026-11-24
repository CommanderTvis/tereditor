using Avalonia.Controls;

using Tereditor.Plugins.Api.UI;

namespace Tereditor.UI;

public sealed partial class WorkbenchWindow
{
    /// <summary>The Agent pane's control when it has been shown in this window.</summary>
    internal Control? AgentPaneControl => toolContent.GetValueOrDefault("agent");

    private Control AgentPanel() => new Panels.AgentPane(Plugins, () => workspaceRoot, FocusedAgentTab, Mount);

    // The selected tab of the last-focused document group, named for the pane: a tool tab or nothing at home holds no agent.
    private (string Key, TabRef Tab)? FocusedAgentTab()
    {
        if (!WorkspaceMounted || atHome) return null;
        if (Layout.Selected(Layout.View.FocusedCenter) is not { IsTool: false } tab) return null;
        return (workspaceRoot + "|" + tab.Id, TabRef(tab, workspaceRoot));
    }

    // Layout, plugin and workspace changes all reread the focus: the pane compares keys itself.
    private void SyncAgentPane() => (toolContent.GetValueOrDefault("agent") as Panels.AgentPane)?.Sync();
}