using System.Net;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;
using Tereditor.Host.Core.Plugins;
using Tereditor.Plugins.Tailscale;

namespace Tereditor.Host.Remote;

/// <summary>A runtime-switchable WebSocket RPC listener over a caller-owned embedded host.</summary>
public sealed class HostListener(string root, HostStateStore state, ITerminalService? terminals, PluginRuntime plugins) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private WebApplication? server;
    private Uri? endpoint;
    private bool disposed;
    private TailscaleServing? tailscale;
    private readonly SemaphoreSlim pairingGate = new(1, 1);
    private Uri? pairingEndpoint;
    // Pairing's own listener on the tailnet address, for when the shared one cannot be reached there.
    private WebApplication? tailnet;
    private Uri? tailnetEndpoint;

    public void ConfigureTailscale(TailscaleServing serving)
    {
        tailscale = serving;
        serving.EnsureListening = async (address, cancellationToken) =>
        {
            await pairingGate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                // With Serve on start on, the shared listener is the user's: pairing never takes its place.
                if (Endpoint is null && !state.Current.Settings.ServeOnStart)
                {
                    await StartAsync(IPAddress.Parse(address), 0, Token, cancellationToken);
                    pairingEndpoint = Endpoint;
                }
                if (Endpoint is not null && (Address.Equals(IPAddress.Any) || Address.Equals(IPAddress.IPv6Any) || Address.ToString() == address))
                    return new UriBuilder("http", address, Port).Uri.ToString();
                return await ListenOnTailnetAsync(IPAddress.Parse(address), cancellationToken);
            }
            finally { pairingGate.Release(); }
        };
        serving.StopListening = async () =>
        {
            await pairingGate.WaitAsync();
            try
            {
                if (pairingEndpoint is not null && ReferenceEquals(Endpoint, pairingEndpoint)) await StopAsync();
                pairingEndpoint = null;
                await StopTailnetAsync();
            }
            finally { pairingGate.Release(); }
        };
    }

    /// <summary>Serves paired devices beside the shared listener, which stays as it is. Called under the pairing gate.</summary>
    private async Task<string> ListenOnTailnetAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (tailnetEndpoint is { } current && current.Host == address.ToString()) return current.ToString();
        await StopTailnetAsync();
        // Nobody learns this token, so only approved devices' own secrets authenticate here.
        var candidate = RemoteServer.CreateListener(root, address, 0, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), state, terminals, plugins, tailscale);
        try { await candidate.StartAsync(cancellationToken); }
        catch
        {
            await candidate.DisposeAsync();
            throw;
        }
        tailnet = candidate;
        tailnetEndpoint = new UriBuilder("http", address.ToString(), new Uri(Bound(candidate)).Port).Uri;
        return tailnetEndpoint.ToString();
    }

    private async Task StopTailnetAsync()
    {
        if (tailnet is not { } active) return;
        tailnet = null;
        tailnetEndpoint = null;
        try { await active.StopAsync().ConfigureAwait(false); }
        finally { await active.DisposeAsync().ConfigureAwait(false); }
    }

    private static string Bound(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    /// <summary>Requests consent in the owning app; absent a handler, browser approval is denied.</summary>
    public Func<string, CancellationToken, Task<bool>>? ConfirmLocalBrowserConnection { get; set; }
    /// <summary>Ephemeral local browser grants for the current listener, or null while stopped.</summary>
    public LocalBrowserConnections? BrowserConnections { get; private set; }

    public IPAddress Address { get; private set; } = IPAddress.Loopback;
    public int Port { get; private set; } = 54123;
    public string Token { get; private set; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public Uri? Endpoint => Volatile.Read(ref endpoint);
    /// <summary>Why the automatic start at launch failed, or null; a later start or stop clears it.</summary>
    public string? StartFailure { get; private set; }
    /// <summary>Raised after a listener starts or stops, on a background thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts serving once, on the default loopback address, port and session token, when the Serve on start setting is on.
    /// A failure is kept in <see cref="StartFailure"/> for the Host page instead of being thrown.
    /// </summary>
    public async Task StartOnLaunchAsync(int? port = null)
    {
        if (!state.Current.Settings.ServeOnStart || Endpoint is not null) return;
        try { await StartAsync(Address, port ?? Port, Token); }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            StartFailure = failure.Message;
            Changed?.Invoke();
        }
    }

    /// <summary>Starts listening without moving the embedded client's adapters onto the wire.</summary>
    public Task StartAsync(IPAddress address, int port, string token, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (server is not null) throw new InvalidOperationException("The host is already listening.");
            var browsers = new LocalBrowserConnections((origin, ct) => ConfirmLocalBrowserConnection?.Invoke(origin, ct) ?? Task.FromResult(false));
            browsers.Changed += BrowserConnectionsChanged;
            WebApplication? candidate = null;
            try
            {
                candidate = RemoteServer.CreateListener(root, address, port, token, state, terminals, plugins, tailscale, browsers);
                await candidate.StartAsync(cancellationToken);
                var listening = Bound(candidate);
                server = candidate;
                BrowserConnections = browsers;
                Address = address; Port = new Uri(listening).Port; Token = token;
                Volatile.Write(ref endpoint, new Uri(listening));
                StartFailure = null;
            }
            catch
            {
                if (candidate is not null) await candidate.DisposeAsync();
                browsers.Changed -= BrowserConnectionsChanged;
                browsers.Dispose();
                throw;
            }
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }, cancellationToken);

    /// <summary>Disconnects remote clients while the embedded host and its shells continue running.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(cancellationToken);
        try { await StopCore(); StartFailure = null; }
        finally { gate.Release(); }
        Changed?.Invoke();
    }, cancellationToken);

    private void BrowserConnectionsChanged() => Changed?.Invoke();

    private async Task StopCore()
    {
        if (server is not { } active) return;
        try { await active.StopAsync(); }
        finally
        {
            await active.DisposeAsync();
            server = null;
            if (BrowserConnections is { } browsers)
            {
                browsers.Changed -= BrowserConnectionsChanged;
                browsers.Dispose();
                BrowserConnections = null;
            }
            Volatile.Write(ref endpoint, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Pairing holds its gate while it takes the other, so this takes them one after the other.
        await pairingGate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; await StopTailnetAsync().ConfigureAwait(false); }
        finally { pairingGate.Release(); }
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; await StopCore().ConfigureAwait(false); }
        finally { gate.Release(); }
        Changed?.Invoke();
    }
}