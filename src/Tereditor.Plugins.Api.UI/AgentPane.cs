using Avalonia.Controls;

namespace Tereditor.Plugins.Api.UI;

/// <summary>The four tabs the Agent pane always shows, the same for every agent.</summary>
public enum AgentPaneTab
{
    /// <summary>What the agent loads: instructions, memory, context usage.</summary>
    Context,

    /// <summary>The agent's settings for this workspace.</summary>
    Settings,

    /// <summary>What the agent can use: tools, skills, commands, servers, packages.</summary>
    Capabilities,

    /// <summary>The account, plan and usage the agent runs under.</summary>
    Account
}

/// <summary>One agent an agent plugin hosts under a single chip, such as one ACP agent.</summary>
/// <param name="Id">The sub-agent's id within the plugin.</param>
/// <param name="Label">The name the picker shows.</param>
public sealed record AgentSubAgent(string Id, string Label);

/// <summary>
/// Registers an agent's content for the Agent pane: one chip in the pane's chip row and the four tabs under it. The host draws the chips,
/// the tabs and the picker and follows the focused document; the plugin supplies one control that shows the tab it is told to show.
/// </summary>
/// <param name="Id">The agent's id within the plugin; with the plugin id it names the chip.</param>
/// <param name="Label">The chip's text.</param>
/// <param name="Icon">A Remix Icon name or <c>asset:&lt;path&gt;</c> for the chip.</param>
/// <param name="Create">Creates the agent's content for a workspace id, shown in the pane's body.</param>
public sealed record AgentPaneRegistration(string Id, string Label, string Icon, Func<string, Control> Create)
{
    /// <summary>Whether the agent is usable now: installed and not blocked. Null means always. Re-read when the plugin calls <see cref="IPluginUIContext.Invalidate"/>.</summary>
    [PluginCrossing(PluginCrossing.Live)]
    public Func<bool>? Usable { get; init; }

    /// <summary>Whether a tab holds this agent: a terminal running it, or a chat document of it. Null means never. Reads in-memory state only.</summary>
    [PluginCrossing(PluginCrossing.Resolver)]
    public Func<TabRef, bool>? Owns { get; init; }

    /// <summary>For a plugin with <see cref="SubAgents"/>: the sub-agent a tab holds, or null when unknown.</summary>
    [PluginCrossing(PluginCrossing.Resolver)]
    public Func<TabRef, string?>? SubAgentOf { get; init; }

    /// <summary>Makes the control show a tab. Called on the UI thread when the user or the focus changes the tab.</summary>
    [PluginCrossing(PluginCrossing.Live, IsolatedShape = "called with the control the factory returned; the plugin process passes its own control")]
    public Action<Control, AgentPaneTab>? ShowTab { get; init; }

    /// <summary>Points the control at another workspace, with the meaning of <see cref="SideToolRegistration.Retarget"/>; false asks for a new control.</summary>
    [PluginCrossing(PluginCrossing.Live, IsolatedShape = "called with the control the factory returned; the plugin process passes its own control")]
    public Func<Control, string, bool>? Retarget { get; init; }

    /// <summary>The sub-agents the picker lists for a workspace; null for an agent without any.</summary>
    [PluginCrossing(PluginCrossing.Live)]
    public Func<string, IReadOnlyList<AgentSubAgent>>? SubAgents { get; init; }

    /// <summary>Tells the control which sub-agent to describe; null means the plugin's default. Called when a tab or the picker chooses one.</summary>
    [PluginCrossing(PluginCrossing.Live, IsolatedShape = "called with the control the factory returned; the plugin process passes its own control")]
    public Action<Control, string?>? SelectSubAgent { get; init; }
}
