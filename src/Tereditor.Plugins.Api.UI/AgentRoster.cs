using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Api.UI;

/// <summary>The state of the plugin that owns an agent.</summary>
public enum AgentPluginState
{
    /// <summary>The plugin is on.</summary>
    Active,

    /// <summary>The plugin is enabled, but loading or activation failed.</summary>
    Failed,

    /// <summary>The plugin is turned off.</summary>
    Disabled,

    /// <summary>The plugin cannot load here, for example in a browser tab.</summary>
    Refused,

    /// <summary>The host does not know the plugin.</summary>
    Absent
}

/// <summary>One agent in the <see cref="AgentRoster"/>.</summary>
/// <param name="Id">The stable agent id: the owning plugin's id for the built-in agents (<c>claude-code</c>, <c>codex</c>, <c>omp</c>, <c>pi</c>), else the launcher's id.</param>
/// <param name="LauncherId">The id of the agent's launcher, as <see cref="IPluginUIContext.AgentLaunchContributions"/> takes it (<c>claude</c> for Claude Code).</param>
/// <param name="Name">The agent's display name.</param>
/// <param name="PluginId">The owning plugin's id, or <see langword="null"/> for a launcher of a plugin the roster does not know.</param>
/// <param name="PluginState">The owning plugin's state.</param>
/// <param name="Installed"><see langword="true"/> when the agent's program was found, <see langword="false"/> when not, <see langword="null"/> when nobody has decided (no probe yet, or the command line cannot be probed, or the plugin is off).</param>
/// <param name="Reason">A plain sentence saying why the launcher cannot be used now (program not found, or a blocked launch contribution), or <see langword="null"/>.</param>
public sealed record AgentRosterEntry(string Id, string LauncherId, string Name, string? PluginId, AgentPluginState PluginState, bool? Installed, string? Reason)
{
    /// <summary>Whether the owning plugin is on (active, or enabled but failed).</summary>
    public bool PluginOn => PluginState is AgentPluginState.Active or AgentPluginState.Failed;

    /// <summary>Whether the agent should be offered: its plugin is on and its program was not found missing.</summary>
    public bool Offered => PluginOn && Installed != false;
}

/// <summary>
/// The agent roster: the agents that tereditor can start, with their plugin's state and whether their program is
/// installed. It is read-only, runs no process and does no file work: it reads the launchers' cached availability, which
/// each agent's plugin refreshes from a probe on the host and announces when it changes. Any plugin reads it through its
/// context.
/// </summary>
public static class AgentRoster
{
    private static readonly (string Id, string LauncherId, string Name)[] Known =
    [
        ("claude-code", "claude", "Claude Code"),
        ("codex", "codex", "Codex"),
        ("omp", "omp", "omp"),
        ("pi", "pi", "Pi")
    ];

    /// <summary>The roster now, on the UI thread: the built-in agents (also when their plugin is off), then other registered launchers.</summary>
    /// <param name="context">The plugin's context.</param>
    public static IReadOnlyList<AgentRosterEntry> Read(IPluginUIContext context) => Build(context.Launchers(), context.Host().Roster);

    /// <summary>
    /// Observes the roster: calls <paramref name="handler"/> on the UI thread with the new roster when an entry, its
    /// plugin's state or its availability changes. Not called for the roster current at registration.
    /// </summary>
    /// <param name="context">The plugin's context.</param>
    /// <param name="handler">Called with the new roster.</param>
    /// <returns>Disposing it stops the observation.</returns>
    public static IDisposable Watch(IPluginUIContext context, Action<IReadOnlyList<AgentRosterEntry>> handler)
    {
        var last = Read(context);
        void Check()
        {
            var next = Read(context);
            if (next.SequenceEqual(last)) return;
            last = next;
            handler(next);
        }
        var launchers = context.OnLaunchersChanged(_ => Check());
        var plugins = context.WatchHost(host => string.Join('\n', host.Roster.Select(entry => entry.Id + ":" + entry.Status)), (_, _) => Check());
        return new Stop(launchers, plugins);
    }

    /// <summary>Builds the roster from the registered launchers and the plugin roster.</summary>
    /// <param name="launchers">The registered launchers.</param>
    /// <param name="plugins">The plugin roster.</param>
    public static IReadOnlyList<AgentRosterEntry> Build(IReadOnlyList<AgentLauncher> launchers, IReadOnlyList<PluginRosterEntry> plugins)
    {
        var entries = new List<AgentRosterEntry>();
        foreach (var (id, launcherId, name) in Known)
        {
            var launcher = launchers.FirstOrDefault(candidate => candidate.Id == launcherId);
            entries.Add(Entry(id, launcherId, launcher?.Label ?? name, id, State(plugins, id), launcher));
        }
        foreach (var launcher in launchers.Where(candidate => Known.All(known => known.LauncherId != candidate.Id)))
            entries.Add(Entry(launcher.Id, launcher.Id, launcher.Label, null, AgentPluginState.Active, launcher));
        return entries;
    }

    private static AgentRosterEntry Entry(string id, string launcherId, string name, string? pluginId, AgentPluginState state, AgentLauncher? launcher)
    {
        if (launcher is null || state is not (AgentPluginState.Active or AgentPluginState.Failed)) return new(id, launcherId, name, pluginId, state, null, null);
        LauncherAvailability availability;
        try { availability = launcher.Availability(); }
        catch (Exception error) when (error is not OperationCanceledException) { availability = new(false, error.Message); }
        return new(id, launcherId, name, pluginId, state, availability.Installed, availability.Available ? null : availability.Reason);
    }

    private static AgentPluginState State(IReadOnlyList<PluginRosterEntry> plugins, string pluginId) =>
        plugins.FirstOrDefault(entry => entry.Id == pluginId)?.Status switch
        {
            PluginStatus.Active => AgentPluginState.Active,
            PluginStatus.Failed => AgentPluginState.Failed,
            PluginStatus.Disabled => AgentPluginState.Disabled,
            PluginStatus.Refused => AgentPluginState.Refused,
            _ => AgentPluginState.Absent
        };

    private sealed class Stop(params IDisposable[] parts) : IDisposable
    {
        public void Dispose()
        {
            foreach (var part in parts) part.Dispose();
        }
    }
}
