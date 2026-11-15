using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Acp.UI;

/// <summary>
/// Which logo an ACP chat tab wears: the registry icon of the agent the chat talks to, drawn in the tab's text colour. A chat with no known
/// agent, an agent with no icon on disk (a custom one, or one the registry never gave) and a tab of another document keep the generic ACP icon.
/// Only icons already on disk are used; nothing is fetched here.
/// </summary>
internal sealed class AcpTabIcons
{
    private readonly Lock gate = new();
    private readonly HashSet<string> asked = [];
    private readonly Dictionary<(string Workspace, string Chat), string> agents = [];
    private IReadOnlyDictionary<string, string> icons = new Dictionary<string, string>();

    private bool Has(string agent) => icons.TryGetValue(agent, out var svg) && AcpGlyph.HasIcon(svg);

    /// <summary>Remembers the agent a chat talks to. True when that changed what a tab shows.</summary>
    public bool SetAgent(string workspace, string chat, string agent)
    {
        lock (gate)
        {
            var changed = !agents.TryGetValue((workspace, chat), out var known) || known != agent;
            agents[(workspace, chat)] = agent;
            return changed && Has(agent);
        }
    }

    /// <summary>Takes the agents of a workspace's chats as the host reports them. True when a tab may now look different.</summary>
    public bool SetAgents(string workspace, IReadOnlyDictionary<string, string> chats)
    {
        lock (gate)
        {
            var changed = false;
            foreach (var (chat, agent) in chats)
            {
                changed |= (!agents.TryGetValue((workspace, chat), out var known) || known != agent) && Has(agent);
                agents[(workspace, chat)] = agent;
            }
            return changed;
        }
    }

    /// <summary>Takes the icons on disk, by agent id. True when one a chat uses changed.</summary>
    public bool SetIcons(IReadOnlyDictionary<string, string> svgs)
    {
        lock (gate)
        {
            var changed = agents.Values.Distinct().Any(agent => icons.GetValueOrDefault(agent) != svgs.GetValueOrDefault(agent));
            icons = svgs;
            return changed;
        }
    }

    /// <summary>Adds the icons the host fetched for chats that just started, keeping the others. True when one a chat uses changed.</summary>
    public bool AddIcons(IReadOnlyDictionary<string, string> svgs)
    {
        lock (gate)
        {
            var merged = new Dictionary<string, string>(icons);
            foreach (var (agent, svg) in svgs) merged[agent] = svg;
            var changed = svgs.Keys.Any(agent => icons.GetValueOrDefault(agent) != merged[agent] && agents.Values.Contains(agent));
            icons = merged;
            return changed;
        }
    }

    /// <summary>The SVG for a chat's tab, or null for the generic icon.</summary>
    public string? SvgFor(string workspace, string chat)
    {
        lock (gate)
            return agents.TryGetValue((workspace, chat), out var agent) && icons.TryGetValue(agent, out var svg) && AcpGlyph.HasIcon(svg) ? svg : null;
    }

    /// <summary>Called once per workspace, the first time a chat tab there has no known agent (a restored tab); it asks the host in the background.</summary>
    public Action<string>? AskAgents { get; init; }

    /// <summary>The tab decorator: an ACP chat tab with a logo; every other tab is left alone.</summary>
    public TabDecoration? Decorate(TabRef tab)
    {
        if (tab is not PluginDocumentTabRef { Scheme: AcpContract.DocumentScheme } chat) return null;
        bool ask;
        lock (gate) ask = !agents.ContainsKey((chat.WorkspaceId, chat.Id)) && asked.Add(chat.WorkspaceId);
        if (ask) AskAgents?.Invoke(chat.WorkspaceId);
        return SvgFor(chat.WorkspaceId, chat.Id) is { } svg ? new TabDecoration { IconSvg = svg } : null;
    }
}