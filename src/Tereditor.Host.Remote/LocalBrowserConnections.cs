using System.Net;
using System.Security.Cryptography;

using Tereditor.Host.Protocol;

namespace Tereditor.Host.Remote;

/// <summary>
/// In-memory browser credentials owned by one listener generation. A credential comes from consent in the owning app or, for a
/// host without a window, from a one-time pairing code; the host keeps only the credential's SHA-256, its origin and its expiry.
/// </summary>
public sealed class LocalBrowserConnections(Func<string, CancellationToken, Task<bool>> confirm, PairingCodes? pairing = null,
    IEnumerable<string>? extraOrigins = null, Func<long>? clock = null) : IDisposable
{
    /// <summary>A credential unused for this long stops working; each accepted call renews it.</summary>
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(1);
    /// <summary>A credential never works longer than this, however busy; the page pairs again.</summary>
    internal static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(12);

    private sealed record Grant(string Origin, long Issued, long Expires);

    private readonly object sync = new();
    private readonly Dictionary<string, Grant> grants = [];
    private readonly HashSet<string> allowed = [LocalBrowserOrigins.Published, .. extraOrigins ?? []];
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private readonly SemaphoreSlim pending = new(1, 1);
    private readonly CancellationTokenSource stopped = new();
    private long nextRequest;

    /// <summary>Approved browser origins; credentials are never exposed in this projection.</summary>
    public IReadOnlyList<string> Origins { get { lock (sync) { Sweep(); return [.. grants.Values.Select(grant => grant.Origin).Distinct().Order()]; } } }
    /// <summary>The SHA-256 keys the credentials are kept under; no credential is ever held in clear.</summary>
    internal IReadOnlyCollection<string> StoredKeys { get { lock (sync) return [.. grants.Keys]; } }
    /// <summary>Raised off the UI thread when approvals change.</summary>
    public event Action? Changed;

    /// <summary>Revokes every approved session from an origin, including active RPC calls.</summary>
    public void Revoke(string origin)
    {
        lock (sync)
            foreach (var key in grants.Where(pair => pair.Value.Origin == origin).Select(pair => pair.Key).ToArray()) grants.Remove(key);
        Changed?.Invoke();
    }

    internal bool Authenticate(string token, HttpContext context)
    {
        if (!IsLocalRequest(context)) return false;
        var key = Key(token);
        lock (sync)
        {
            if (stopped.IsCancellationRequested || !grants.TryGetValue(key, out var grant)) return false;
            var time = now();
            if (time >= grant.Expires || grant.Origin != context.Request.Headers.Origin.ToString()) { if (time >= grant.Expires) grants.Remove(key); return false; }
            grants[key] = grant with { Expires = Math.Min(time + (long)IdleLifetime.TotalMilliseconds, grant.Issued + (long)MaxLifetime.TotalMilliseconds) };
            return true;
        }
    }

    private static string Key(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private void Sweep()
    {
        var time = now();
        foreach (var key in grants.Where(pair => time >= pair.Value.Expires).Select(pair => pair.Key).ToArray()) grants.Remove(key);
    }

    private static bool IsLoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    // Both TCP endpoints and the Host header are loopback (a rebound DNS name fails here), and the Origin is canonical and either a
    // loopback origin or one of the exact public origins this host allows.
    private bool IsLocalRequest(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
            context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local) &&
            IsLoopbackHost(context.Request.Host.Host) &&
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            uri.UserInfo.Length == 0 && origin == uri.GetLeftPart(UriPartial.Authority) &&
            (IsLoopbackHost(uri.Host) || allowed.Contains(origin));
    }

    internal void Map(WebApplication app)
    {
        app.Lifetime.ApplicationStopping.Register(() => { stopped.Cancel(); lock (sync) grants.Clear(); Changed?.Invoke(); });
        app.MapMethods("/browser/connect", ["POST", "OPTIONS"], async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Vary = "Origin";
            if (!IsLocalRequest(context)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            context.Response.Headers.AccessControlAllowOrigin = context.Request.Headers.Origin;
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.Headers.AccessControlAllowMethods = "POST";
                context.Response.Headers.AccessControlAllowHeaders = "content-type";
                // Chrome asks a public page's request to a loopback host for this; the Host and Origin checks above already hold.
                if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network")) context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            if (!await pending.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
            string? token = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopped.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                lock (sync)
                {
                    Sweep();
                    if (grants.Count >= 32 || now() < nextRequest)
                    { context.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
                }
                var origin = context.Request.Headers.Origin.ToString();
                var code = await ReadCode(context, timeout.Token);
                // A request with a code is a pairing attempt and never prompts; one without asks the app.
                var approved = code is not null
                    ? pairing is not null && pairing.Redeem(code)
                    : await confirm(origin, timeout.Token).WaitAsync(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (!approved)
                {
                    lock (sync) nextRequest = now() + 2000;
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                lock (sync)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                    var time = now();
                    grants.Add(Key(token), new Grant(origin, time, time + (long)IdleLifetime.TotalMilliseconds));
                }
                Changed?.Invoke();
                await context.Response.WriteAsJsonAsync(new LocalBrowserApprovalReply(token), LocalBrowserApprovalJson.Default.LocalBrowserApprovalReply, cancellationToken: timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (token is not null) { lock (sync) grants.Remove(Key(token)); Changed?.Invoke(); }
                if (!context.RequestAborted.IsCancellationRequested) context.Response.StatusCode = stopped.IsCancellationRequested ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status408RequestTimeout;
            }
            finally { pending.Release(); }
        });
    }

    // The code travels in the body only, never in a URL. An empty body is an approval request; a malformed one is a wrong code.
    private static async Task<string?> ReadCode(HttpContext context, CancellationToken cancellationToken)
    {
        // A chunked body has no length, so read at most 257 bytes and judge by what arrived.
        var buffer = new byte[257];
        var length = 0;
        while (length < buffer.Length && await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken) is var read and > 0) length += read;
        if (length == 0) return null;
        if (length > 256) return "";
        try { return System.Text.Json.JsonSerializer.Deserialize(buffer.AsSpan(0, length), LocalBrowserApprovalJson.Default.LocalBrowserPairingRequest)?.Code ?? ""; }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    public void Dispose() => stopped.Dispose();
}