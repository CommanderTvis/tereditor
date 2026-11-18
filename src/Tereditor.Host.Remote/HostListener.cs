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

    public void ConfigureTailscale(TailscaleServing serving)
    {
        tailscale = serving;
        serving.EnsureListening = async (address, cancellationToken) =>
        {
            await pairingGate.WaitAsync(cancellationToken);
            try
            {
                if (Endpoint is null)
                {
                    await StartAsync(IPAddress.Parse(address), 0, Token, cancellationToken);
                    pairingEndpoint = Endpoint;
                }
                if (!Address.Equals(IPAddress.Any) && !Address.Equals(IPAddress.IPv6Any) && Address.ToString() != address)
                    throw new IOException("Stop the existing listener in Settings › Host so Tailscale can start one on its network address.");
                return new UriBuilder("http", address, Port).Uri.ToString();
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
            }
            finally { pairingGate.Release(); }
        };
    }

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
                var listening = candidate.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
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
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; await StopCore().ConfigureAwait(false); }
        finally { gate.Release(); }
        Changed?.Invoke();
    }
}