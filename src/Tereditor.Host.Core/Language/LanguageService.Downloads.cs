using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core.Language;

public sealed partial class LanguageService
{
    /// <summary>Whether the Runtimes for tooling plugin may download a runtime.</summary>
    internal Func<RuntimeOffer, CancellationToken, ValueTask<bool>>? RuntimeConsent { set => servers.RuntimeConsent = value; }

    internal Func<bool> RuntimesEnabled { init => servers.RuntimesEnabled = value; }

    /// <summary>The path the user pinned for a runtime's program, by executable name; empty when none.</summary>
    internal Func<string, string> RuntimePin { init => servers.RuntimePin = value; }

    internal Task<RuntimeResolution> ResolveRuntimeAsync(RuntimeKind kind, string workspace, CancellationToken token) => servers.ResolveRuntimeAsync(kind, workspace, token);

    internal Task<RuntimeStatus> RuntimeStatusAsync(RuntimeKind kind, CancellationToken token) => servers.StatusAsync(kind, token);

    internal Task DownloadRuntimeAsync(RuntimeKind kind, CancellationToken token) => servers.DownloadRuntimeAsync(kind, token);

    /// <summary>Tells the host a process started from a resolved runtime, so that removing the downloaded copy can stop it first.</summary>
    internal static void TrackRuntimeProcess(RuntimeResolution resolution, int processId)
    {
        if (resolution is { Available: true, Source: RuntimeSource.Downloaded }) ManagedProcesses.Track(processId, resolution.Command);
    }

    /// <summary>Stops every language server that runs on a runtime (Bun), whichever copy it uses; they start again on demand and then find the runtime or say what is missing.</summary>
    private async Task StopRuntimeUsersAsync(string executable, string reason)
    {
        foreach (var key in entries.Keys.ToArray())
            if (servers.Find(key[(key.IndexOf('\0') + 1)..]) is { Spec.Runtime: var runtime } && runtime == executable && entries.TryRemove(key, out var entry))
            {
                try { await PreserveDraftsAsync(key, entry, lifetime.Token); }
                catch (OperationCanceledException) { }
                finally { await StopAsync(entry, reason); }
            }
    }

    /// <summary>Stops the processes that run from a downloaded runtime and deletes the copy and its caches. The user's pinned path stays.</summary>
    internal async Task RemoveRuntimeDownloadAsync(RuntimeKind kind)
    {
        await StopRuntimeUsersAsync(LanguageServers.ExecutableOf(kind), "the downloaded " + LanguageServers.NameOf(kind) + " was removed");
        await Task.Run(() => servers.RemoveRuntimeDownload(kind));
    }

    internal async Task PurgeRuntimeDownloadsAsync()
    {
        foreach (var kind in new[] { RuntimeKind.Bun, RuntimeKind.Uv }) await RemoveRuntimeDownloadAsync(kind);
    }

    /// <summary>Stops a language server in every workspace and deletes what was downloaded for it.</summary>
    internal async Task PurgeServerDownloadsAsync(string serverId)
    {
        foreach (var key in entries.Keys.Where(key => key[(key.IndexOf('\0') + 1)..] == serverId).ToArray())
            if (entries.TryRemove(key, out var entry)) await StopAsync(entry, "its plugin was turned off");
        await Task.Run(() => servers.RemoveServerDownloads(serverId));
    }
}