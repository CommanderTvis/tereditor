using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>
/// Fills the icon of one registry agent when a chat for it starts. It uses the same <see cref="AcpIconCache"/> as the settings page, so the same
/// url rules, size cap, safety checks, timeout and atomic write apply. An icon already on disk is not fetched. Each agent is tried once per run,
/// failures are only logged, and nothing here is awaited by the chat start.
/// </summary>
internal sealed class AcpAgentIcons(string directory, AcpFetch fetch, Func<CancellationToken, Task<JsonObject?>> registry, Action<string> debug)
{
    private readonly ConcurrentDictionary<string, Task<string?>> once = new();

    /// <summary>The agent's icon (null for none), fetched at most once per run; a custom agent does nothing.</summary>
    public Task<string?> EnsureAsync(AcpAgentEntry agent) =>
        agent.Origin == "registry" ? once.GetOrAdd(agent.Id, id => Task.Run(() => RunAsync(id))) : Task.FromResult<string?>(null);

    private async Task<string?> RunAsync(string id)
    {
        try
        {
            if (await registry(CancellationToken.None) is not { } root) return null;
            var url = AcpRegistry.View(root, "cache", null, null, AcpRegistry.Platform()).Agents.FirstOrDefault(agent => agent.Id == id)?.Icon;
            if (url is null) return null;
            var cache = new AcpIconCache(directory, fetch);
            var wanted = new[] { (id, url) };
            var onDisk = await cache.LoadAsync(wanted, false, true, CancellationToken.None);
            if (onDisk.TryGetValue(id, out var have)) return have;
            return (await cache.LoadAsync(wanted, false, false, CancellationToken.None)).GetValueOrDefault(id);
        }
        catch (Exception error)
        {
            debug("could not fetch the icon of an ACP agent: " + error.GetType().Name);
            return null;
        }
    }
}