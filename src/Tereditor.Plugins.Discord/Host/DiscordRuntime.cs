namespace Tereditor.Plugins.Discord.Host;

/// <summary>
/// Owns one Discord IPC connection for an activation's lifetime: connecting on demand, a retry floor after a
/// failure, the elapsed-time anchor per project, and announcing every status change.
/// </summary>
internal sealed class DiscordRuntime(Func<DiscordSettings> settings, Action<DiscordStatus> announce)
{
    private const long RetryFloorMilliseconds = 5_000;
    private readonly Lock gate = new();
    private DiscordIpc? ipc;
    private DiscordPresence? latest;
    private long projectStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private string? startedForProjectId;
    private string? failure;
    private long lastAttempt = long.MinValue / 2;
    private Task? connectTask;
    private DiscordActivity? acknowledged;
    private readonly SemaphoreSlim publishing = new(1, 1);
    private bool stopped;
    private int connectionGeneration;

    private bool IsConnected => ipc?.Connected == true;

    private DiscordStatus Current()
    {
        lock (gate)
        {
            var decision = PresenceDecision.For(latest, settings(), projectStartedAt);
            // A presence counts as published only once Discord has answered it.
            var shown = IsConnected && (decision is not PresenceDecision.Publish publish || publish.Activity == acknowledged);
            return PresenceDecision.Status(decision, shown, failure);
        }
    }

    private DiscordStatus Report()
    {
        var next = Current();
        lock (gate)
            if (!stopped) announce(next);
        return next;
    }

    private void Disconnect()
    {
        DiscordIpc? closing;
        lock (gate) { closing = ipc; ipc = null; failure = null; acknowledged = null; connectionGeneration++; }
        closing?.Close();
    }

    private void TakeLastError()
    {
        lock (gate)
            if (ipc?.LastError is { } error) { failure = error; ipc.LastError = null; }
    }

    // Callers during a connection attempt wait for it, so none returns early and drops its presence.
    private Task EnsureConnectedAsync(string applicationId)
    {
        lock (gate)
        {
            if (stopped || IsConnected) return Task.CompletedTask;
            if (connectTask is not null) return connectTask;
            if (failure is not null && Environment.TickCount64 - lastAttempt < RetryFloorMilliseconds) return Task.CompletedTask;
            lastAttempt = Environment.TickCount64;
            return connectTask = ConnectAsync(applicationId, connectionGeneration);
        }
    }

    private async Task ConnectAsync(string applicationId, int generation)
    {
        await Task.Yield();
        var client = new DiscordIpc();
        try
        {
            await client.ConnectAsync(applicationId, () =>
            {
                lock (gate)
                {
                    if (!ReferenceEquals(ipc, client)) return;
                    ipc = null;
                    acknowledged = null;
                    failure = "Discord closed the connection.";
                }
                Report();
            });
            lock (gate)
            {
                if (stopped || generation != connectionGeneration) client.Close();
                else { ipc = client; failure = null; acknowledged = null; }
            }
        }
        catch (InvalidOperationException error)
        {
            client.Close();
            lock (gate)
                if (!stopped && generation == connectionGeneration) failure = error.Message;
        }
        finally
        {
            lock (gate) connectTask = null;
        }
    }

    public async Task<DiscordStatus> PublishPresenceAsync(DiscordPresence? presence)
    {
        long startedAt;
        lock (gate)
        {
            if (stopped) return Current();
            // The elapsed time follows the project, not the file.
            if (presence?.ProjectId != startedForProjectId)
            {
                startedForProjectId = presence?.ProjectId;
                projectStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            latest = presence;
            startedAt = projectStartedAt;
        }
        var current = settings();
        var decision = PresenceDecision.For(presence, current, startedAt);
        if (decision is PresenceDecision.Silent)
        {
            Disconnect();
            return Report();
        }
        await SyncAsync();
        return Report();
    }

    // Sends whatever the latest presence decides, one at a time, so a slow earlier call cannot overwrite a newer one.
    private async Task SyncAsync()
    {
        await publishing.WaitAsync();
        try
        {
            var current = settings();
            await EnsureConnectedAsync(current.ApplicationId);
            DiscordIpc? client;
            PresenceDecision decision;
            lock (gate)
            {
                if (stopped) return;
                client = ipc;
                decision = PresenceDecision.For(latest, current, projectStartedAt);
            }
            if (client is null || decision is PresenceDecision.Silent) return;
            var activity = decision is PresenceDecision.Publish publish ? publish.Activity : null;
            try
            {
                await client.SetActivityAsync(activity);
                lock (gate)
                    if (!stopped && ReferenceEquals(ipc, client)) { acknowledged = activity; failure = null; }
            }
            catch (InvalidOperationException error)
            {
                lock (gate)
                    if (!stopped && ReferenceEquals(ipc, client)) failure = error.Message;
            }
            TakeLastError();
        }
        finally { publishing.Release(); }
    }

    /// <summary>Answers truthfully: it retries a connection rather than reporting a cached failure.</summary>
    public async Task<DiscordStatus> GetStatusAsync()
    {
        var current = settings();
        DiscordPresence? presence;
        long startedAt;
        lock (gate) (presence, startedAt) = (latest, projectStartedAt);
        if (PresenceDecision.For(presence, current, startedAt) is not PresenceDecision.Silent && !IsConnected)
            await SyncAsync();
        TakeLastError();
        return Current();
    }

    /// <summary>A settings change clears the retry floor, so the next attempt is immediate.</summary>
    public void ApplySettingsChange()
    {
        DiscordPresence? presence;
        lock (gate) { failure = null; lastAttempt = long.MinValue / 2; presence = latest; }
        _ = PublishPresenceAsync(presence);
    }

    public void Stop()
    {
        DiscordIpc? closing;
        lock (gate) { stopped = true; closing = ipc; }
        closing?.SetActivity(null);
        Disconnect();
        lock (gate) { latest = null; startedForProjectId = null; }
    }
}