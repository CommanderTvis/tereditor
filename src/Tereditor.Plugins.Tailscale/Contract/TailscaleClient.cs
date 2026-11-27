using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Tailscale;

/// <summary>The client-local bootstrap, usable before any host plugin context exists.</summary>
public sealed class TailscaleClient : IDisposable
{
    private static readonly object pairingFiles = new();

    // The pairing files and the host's replies are read here before any plugin contract is created.
    static TailscaleClient() => PluginJson.Register(TailscaleJson.Default);
    private readonly HttpClient http;
    private readonly Func<string, CancellationToken, Task<Uri>> bootstrap;
    private readonly Func<CancellationToken, Task<IReadOnlyList<TailscalePeer>>> peers;

    public TailscaleClient() : this(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, BootstrapAsync) { }

    /// <summary>Uses a client-owned embedded network rather than the system Tailscale installation.</summary>
    public TailscaleClient(ITailscaleClientNetwork network) : this(network.CreateHandler(), network.BootstrapAsync, network.PeersAsync) { }

    internal TailscaleClient(HttpMessageHandler handler, Func<string, CancellationToken, Task<Uri>> bootstrap,
        Func<CancellationToken, Task<IReadOnlyList<TailscalePeer>>>? peers = null)
    {
        http = new(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 16 * 1024 };
        this.bootstrap = bootstrap;
        this.peers = peers ?? TailscaleCli.PeersAsync;
    }

    internal static async Task<Uri> BootstrapAsync(string host, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate("http://" + host.Trim() + ":" + TailscaleContract.DiscoveryPort, UriKind.Absolute, out var uri) ||
            uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new IOException("Enter a Tailscale device name without a port or path.");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken); }
        catch (System.Net.Sockets.SocketException error)
        {
            throw new IOException("Could not resolve the Tailscale host name. Turn on Tailscale on this device, check that both devices use the same tailnet, and check the host name and MagicDNS.", error);
        }
        var ip = addresses.FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && TailscaleCli.IsTailnetAddress(address))
            ?? throw new IOException("This name does not resolve to a Tailscale address. Check Tailscale and MagicDNS.");
        return new UriBuilder("http", ip.ToString(), TailscaleContract.DiscoveryPort).Uri;
    }

    public async Task<IReadOnlyList<TailscalePeer>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var peers = (await this.peers(cancellationToken)).Take(256).ToArray();
        using var slots = new SemaphoreSlim(8);
        var results = await Task.WhenAll(peers.Select(async peer =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var uri = await bootstrap(peer.Address, timeout.Token);
                using var reply = await http.GetAsync(new Uri(uri, "tereditor"), timeout.Token);
                reply.EnsureSuccessStatusCode();
                var info = await reply.Content.ReadFromJsonAsync(PluginJson.TypeInfo<TailscaleHostInfo>(), timeout.Token);
                return info is null ? null : new TailscalePeer(info.Name, peer.Address);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or JsonException) { return null; }
            finally { slots.Release(); }
        }));
        cancellationToken.ThrowIfCancellationRequested();
        return results.OfType<TailscalePeer>().ToArray();
    }

    public static TailscaleRemembered NewPairing(string host) => new(host.Trim(), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    public async Task<TailscaleConnection> PairAsync(TailscaleRemembered pairing, Action<string>? progress, CancellationToken cancellationToken)
    {
        var uri = await bootstrap(pairing.Host, cancellationToken);
        progress?.Invoke("Approve this device on the host in Settings › Tailscale. Compare code " + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pairing.Token)))[..8].ToUpperInvariant() + ".");
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "tereditor/pair"));
            request.Headers.Add("X-tereditor-Pairing", pairing.Token);
            var reply = await RequestPairAsync(request, cancellationToken);
            switch (reply.State)
            {
                case "approved":
                    if (!Uri.TryCreate(reply.Address, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || endpoint.UserInfo.Length > 0 || endpoint.AbsolutePath != "/" ||
                        endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || !IPAddress.TryParse(endpoint.Host, out var ip) || !TailscaleCli.IsTailnetAddress(ip) || endpoint.Host != uri.Host)
                        throw new IOException("The host returned an invalid Tailscale endpoint.");
                    return new(endpoint.ToString(), pairing.Token);
                case "pending": await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); break;
                case "denied": throw new IOException("The host declined this pairing request. Start a new pairing to try again.");
                case "busy": throw new IOException("Too many pairing requests are waiting on the host. Approve or decline them in Settings › Tailscale on the host, then try again.");
                default: throw new IOException("The host cannot accept this pairing request (" + reply.State + ").");
            }
        }
    }

    /// <summary>One pairing request; every failure comes back as an <see cref="IOException"/> that says what to do.</summary>
    private async Task<TailscalePairReply> RequestPairAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        const string Unreachable = "Could not reach tereditor on this host. Check that tereditor is running there, that its Tailscale plugin is on in Settings › Plugins, and that your tailnet policy allows the connection.";
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (HttpRequestException error) { throw new IOException(Unreachable, error); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw new IOException(Unreachable, error); }
        using (response)
        {
            TailscalePairReply? reply = null;
            try { reply = await response.Content.ReadFromJsonAsync(PluginJson.TypeInfo<TailscalePairReply>(), cancellationToken); }
            catch (JsonException) { }
            if (response.IsSuccessStatusCode)
                return reply ?? throw new IOException("The host sent a pairing reply this app cannot read. Update tereditor on both devices.");
            throw new IOException(response.StatusCode switch
            {
                HttpStatusCode.ServiceUnavailable when reply?.Problem is { Length: > 0 } problem =>
                    "The host approved this device but could not accept the connection. " + problem + " See Settings › Tailscale on the host.",
                HttpStatusCode.ServiceUnavailable => "The host could not complete pairing. Open Settings › Tailscale on the host to see why, then try again.",
                HttpStatusCode.Forbidden => "The host could not verify this device on its tailnet. Check that both devices use the same tailnet, then try again.",
                HttpStatusCode.NotFound => "The host does not offer pairing here. Update tereditor on the host and check that its Tailscale plugin is on.",
                var status => "The host could not complete pairing (HTTP " + (int)status + "). Check Settings › Tailscale on the host."
            });
        }
    }

    public static TailscaleRemembered? Load(string path)
    {
        try { return File.Exists(path) ? PluginJson.Deserialize<TailscaleRemembered>(File.ReadAllText(path)) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Forget(string path, string token)
    {
        lock (pairingFiles)
            if (Load(path)?.Token == token) File.Delete(path);
    }

    public static void Save(string path, TailscaleRemembered pairing, CancellationToken cancellationToken = default)
    {
        lock (pairingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options)) JsonSerializer.Serialize(stream, pairing, PluginJson.TypeInfo<TailscaleRemembered>());
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
    }

    public void Dispose() => http.Dispose();
}