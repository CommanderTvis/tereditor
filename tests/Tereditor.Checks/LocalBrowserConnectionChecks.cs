using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;

using Microsoft.Extensions.DependencyInjection;

using Tereditor.Host.Client;
using Tereditor.Host.Core;
using Tereditor.Host.Core.Plugins;
using Tereditor.Host.Protocol;
using Tereditor.Host.Remote;

namespace Tereditor.Checks;

internal static class LocalBrowserConnectionChecks
{
    internal const string Origin = "http://localhost:54125";

    internal static async Task<HttpResponseMessage> Request(HttpClient client, string? origin = Origin, CancellationToken cancellationToken = default, string? host = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/browser/connect");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        return await client.SendAsync(request, cancellationToken);
    }

    internal static async Task<RpcPeer> Connect(Uri endpoint, string? origin)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RpcWire.Subprotocol);
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            await socket.ConnectAsync(new UriBuilder(endpoint) { Scheme = "ws", Path = "/rpc" }.Uri, CancellationToken.None);
            return new RpcPeer(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static CallContext Context(string token) => new(new CallOptions(new Metadata { { "authorization", "Bearer " + token } }, deadline: DateTime.UtcNow.AddSeconds(15)));

    internal static async Task Handshake(Uri endpoint, string token, string? origin, bool allowed)
    {
        await using var peer = await Connect(endpoint, origin);
        try
        {
            await using var call = await peer.Open("IStateRpc/HandshakeAsync", Context(token));
            await call.Send(new HandshakeRequest());
            await call.End();
            await foreach (var reply in call.Read<HandshakeReply>()) Require(allowed && reply.ProtocolVersion > 0, "An unapproved browser reached RPC.");
            Require(allowed, "An unapproved browser completed an RPC call.");
        }
        catch (RpcException error) when (!allowed && error.StatusCode == StatusCode.Unauthenticated) { }
    }

    internal static async Task RunHost(string root)
    {
        foreach (var address in new[] { "http://localhost:1", "https://127.0.0.1:2", "http://[::1]:3" })
            Require(LocalBrowserApprovalClient.IsLoopback(new Uri(address)), "Local browser approval must support literal IPv4/IPv6 loopback and localhost.");
        foreach (var address in new[] { "http://localhost.evil:1", "http://192.0.2.1:2", "https://example.com", "ftp://localhost" })
            Require(!LocalBrowserApprovalClient.IsLoopback(new Uri(address)), "Local browser approval must not resolve or accept remote targets.");
        try { await LocalBrowserApprovalClient.RequestAsync(new Uri("http://example.com")); throw new InvalidOperationException("Remote target reached local approval bootstrap."); }
        catch (ArgumentException) { }
        var directory = Path.Combine(root, "local-browser-host");
        Directory.CreateDirectory(directory);
        var state = new HostStateStore(null);
        await using var plugins = new PluginRuntime(new() { StateDirectory = null, State = state, Builtins = [] });
        await using var listener = new HostListener(directory, state, null, plugins);
        var requested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConfirmLocalBrowserConnection = (origin, ct) => { requested.TrySetResult(origin); return decision.Task.WaitAsync(ct); };
        await listener.StartAsync(IPAddress.Loopback, 0, "local-browser-master");
        using var client = new HttpClient { BaseAddress = listener.Endpoint };
        foreach (var origin in new string?[] { null, "null", "https://evil.example", "http://localhost.evil:1", "http://localhost:1/path", "http://user@localhost:1", "http://localhost:1/" })
        {
            using var invalid = await Request(client, origin);
            Require(invalid.StatusCode == HttpStatusCode.Forbidden && !invalid.Headers.Contains("Access-Control-Allow-Origin"), "Untrusted origins must be rejected before prompting or enabling CORS.");
        }
        using (var rebound = await Request(client, host: "evil.example")) Require(rebound.StatusCode == HttpStatusCode.Forbidden, "DNS-rebound hosts must not request approval.");
        using (var preflight = await Request(client, method: HttpMethod.Options))
            Require(preflight.StatusCode == HttpStatusCode.NoContent && preflight.Headers.GetValues("Access-Control-Allow-Origin").Single() == Origin, "Preflight must allow only the validated origin.");
        Require(!requested.Task.IsCompleted, "Invalid requests and preflight must never prompt.");
        var denied = Request(client);
        Require(await requested.Task.WaitAsync(TimeSpan.FromSeconds(10)) == Origin && !denied.IsCompleted, "The real origin must await consent.");
        using (var duplicate = await Request(client)) Require(duplicate.StatusCode == HttpStatusCode.TooManyRequests, "Only one approval prompt may be pending.");
        decision.SetResult(false);
        using (var response = await denied) Require(response.StatusCode == HttpStatusCode.Forbidden, "Deny must issue no credential.");
        using (var cooldown = await Request(client)) Require(cooldown.StatusCode == HttpStatusCode.TooManyRequests, "Denied prompts must have a cooldown.");
        await Task.Delay(2100);
        listener.ConfirmLocalBrowserConnection = (_, _) => Task.FromResult(true);
        using var approved = await Request(client);
        var grant = (await approved.Content.ReadFromJsonAsync<LocalBrowserApprovalReply>())!.Token;
        Require(approved.IsSuccessStatusCode && grant.Length == 64 && grant != listener.Token && approved.Headers.CacheControl?.NoStore == true, "Approval must issue a private temporary credential, never the master token.");
        Require(listener.BrowserConnections!.Origins.SequenceEqual([Origin]), "Only approved origins should be listed.");
        await Handshake(listener.Endpoint!, grant, Origin, true);
        await Handshake(listener.Endpoint!, grant, Origin, true);
        await Handshake(listener.Endpoint!, grant, "http://localhost:54126", false);
        await Handshake(listener.Endpoint!, grant, null, false);
        await Handshake(listener.Endpoint!, "wrong", Origin, false);
        await Handshake(listener.Endpoint!, listener.Token, null, true);
        await using (var peer = await Connect(listener.Endpoint!, Origin))
        {
            await using var call = await peer.Open("IStateRpc/WatchAsync", Context(grant));
            await call.Send(new StateRequest());
            await call.End();
            await using var replies = call.Read<StateReply>().GetAsyncEnumerator();
            Require(await replies.MoveNextAsync(), "Approved browser subscriptions must start.");
            listener.BrowserConnections.Revoke(Origin);
            try { await replies.MoveNextAsync(); throw new InvalidOperationException("Revocation left an active subscription open."); }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated) { }
        }
        await Handshake(listener.Endpoint!, grant, Origin, false);
        using var second = await Request(client);
        var oldGrant = (await second.Content.ReadFromJsonAsync<LocalBrowserApprovalReply>())!.Token;
        await listener.StopAsync();
        await listener.StartAsync(IPAddress.Loopback, 0, listener.Token);
        await Handshake(listener.Endpoint!, oldGrant, Origin, false);
        Require(listener.BrowserConnections!.Origins.Count == 0, "Restart must forget every browser approval.");
        using var restarted = new HttpClient { BaseAddress = listener.Endpoint };
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var ignoredDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConfirmLocalBrowserConnection = (origin, ct) =>
        {
            requested.TrySetResult(origin);
            ct.Register(() => cancelled.TrySetResult());
            return ignoredDecision.Task;
        };
        using var cancel = new CancellationTokenSource();
        var pending = Request(restarted, cancellationToken: cancel.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        try { using var ignored = await pending; throw new InvalidOperationException("A canceled request completed."); }
        catch (OperationCanceledException) { }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(listener.BrowserConnections.Origins.Count == 0, "Cancellation must issue no approval.");
        Console.WriteLine("PASS local browser approval: origin/Host gates, CORS, consent, denial, bounded pending requests, temporary tokens, RPC origin binding, reconnect, revocation, restart and cancellation");
    }

    internal static async Task RunPairing(string root)
    {
        const string Pages = LocalBrowserOrigins.Published;
        long time = 1_000_000;
        var pairing = new PairingCodes(clock: () => time);
        string? reissued = null;
        pairing.Reissued += code => reissued = code;
        using var browsers = new LocalBrowserConnections((_, _) => Task.FromResult(false), pairing, clock: () => time);
        Directory.CreateDirectory(Path.Combine(root, "pairing-host"));
        await using var app = RemoteServer.Create(Path.Combine(root, "pairing-host"), IPAddress.Loopback, 0, "pairing-master", browsers: browsers);
        await app.StartAsync();
        var endpoint = new Uri(app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single());
        using var client = new HttpClient { BaseAddress = endpoint };

        async Task<HttpResponseMessage> Pair(string? code, string? origin = Pages, string? host = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/browser/connect");
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (host is not null) request.Headers.Host = host;
            if (code is not null) request.Content = JsonContent.Create(new LocalBrowserPairingRequest(code), LocalBrowserApprovalJson.Default.LocalBrowserPairingRequest);
            return await client.SendAsync(request);
        }
        async Task<HttpStatusCode> Status(string? code, string? origin = Pages, string? host = null) { using var reply = await Pair(code, origin, host); return reply.StatusCode; }
        void Wait() => time += 2100;

        // The code alphabet and shape: 10 symbols in two groups, never the ambiguous I, L, O or U.
        var first = pairing.Issue();
        Require(first.Length == 11 && first[5] == '-' && first.Where(c => c != '-').All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c)), "A pairing code must be two groups of five symbols.");
        Require(first != new PairingCodes().Issue(), "Pairing codes must be random.");

        // Origins and Host headers are checked before any code is looked at, and a refused request leaves the code alive.
        foreach (var origin in new string?[] { null, "null", "https://evil.example", "https://commandertvis.github.io.evil.example", "http://commandertvis.github.io", "https://commandertvis.github.io:8443", "https://commandertvis.github.io/" })
            Require(await Status(first, origin) == HttpStatusCode.Forbidden, "An origin outside the allow-list must not pair: " + origin);
        Require(await Status(first, host: "evil.example") == HttpStatusCode.Forbidden, "A rebound Host header must not pair.");
        Require(await Status(first, host: "commandertvis.github.io") == HttpStatusCode.Forbidden, "A public Host header must not pair.");
        using (var preflight = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/browser/connect") { Headers = { { "Origin", Pages }, { "Access-Control-Request-Private-Network", "true" } } }))
            Require(preflight.StatusCode == HttpStatusCode.NoContent && preflight.Headers.GetValues("Access-Control-Allow-Origin").Single() == Pages && preflight.Headers.GetValues("Access-Control-Allow-Private-Network").Single() == "true", "The preflight must allow the page's origin and private network access.");
        using (var foreign = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/browser/connect") { Headers = { { "Origin", "https://evil.example" } } }))
            Require(foreign.StatusCode == HttpStatusCode.Forbidden && !foreign.Headers.Contains("Access-Control-Allow-Origin"), "A foreign preflight must be refused without CORS.");

        // A wrong code is refused, with a cooldown, and the right one still works.
        Require(await Status("AAAAA-AAAAA") == HttpStatusCode.Forbidden, "A wrong code must be refused.");
        Require(await Status(first) == HttpStatusCode.TooManyRequests, "A failed try must start a cooldown.");
        Wait();
        Require(await Status(null) == HttpStatusCode.Forbidden, "Without a code or an app to ask, pairing must be refused.");
        Wait();
        using var good = await Pair(first.ToLowerInvariant().Replace("-", " "));
        Require(good.StatusCode == HttpStatusCode.OK && good.Headers.CacheControl?.NoStore == true, "The code must pair, ignoring case and separators.");
        var token = (await good.Content.ReadFromJsonAsync(LocalBrowserApprovalJson.Default.LocalBrowserApprovalReply))!.Token;
        Require(token.Length == 64 && token != "pairing-master", "Pairing must issue a 256-bit credential, never the master token.");
        Require(browsers.StoredKeys.Count == 1 && !browsers.StoredKeys.Contains(token) && browsers.StoredKeys.Single() == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))), "Credentials must be kept as SHA-256 only.");
        Require(browsers.Origins.SequenceEqual([Pages]), "The paired origin must be listed.");
        await Handshake(endpoint, token, Pages, true);
        await Handshake(endpoint, token, "https://evil.example", false);
        await Handshake(endpoint, token, "http://localhost:3000", false);
        await Handshake(endpoint, token, null, false);

        // A code works once; use replaced it.
        Wait();
        Require(await Status(first) == HttpStatusCode.Forbidden, "A used code must not pair again.");
        Require(reissued is { } && reissued != first, "Use must make a new code available to the owner.");

        // Five wrong tries burn the live code, which the refused reuse above already cost one; the replacement is another code.
        var live = reissued!;
        for (var i = 0; i < 4; i++) { Wait(); Require(await Status("ZZZZZ-ZZZZZ") == HttpStatusCode.Forbidden, "A wrong code must be refused."); }
        Wait();
        Require(await Status(live) == HttpStatusCode.Forbidden, "Five wrong tries must burn the code.");
        var replacement = reissued!;
        Require(replacement != live, "A burned code must be replaced.");

        // An expired code is refused like a wrong one.
        time += (long)TimeSpan.FromMinutes(5).TotalMilliseconds + 1;
        Require(await Status(replacement) == HttpStatusCode.Forbidden, "An expired code must not pair.");
        Wait();
        var fresh = pairing.Issue();
        using var second = await Pair(fresh);
        var secondToken = (await second.Content.ReadFromJsonAsync(LocalBrowserApprovalJson.Default.LocalBrowserApprovalReply))!.Token;
        Require(second.IsSuccessStatusCode && secondToken != token, "A new code must pair with a new credential.");

        // Revocation, idle expiry with renewal, and the absolute limit.
        browsers.Revoke(Pages);
        await Handshake(endpoint, token, Pages, false);
        Require(browsers.StoredKeys.Count == 0, "Revoking an origin must drop its credentials.");
        Wait();
        var third = pairing.Issue();
        using var renewed = await Pair(third);
        var renewedToken = (await renewed.Content.ReadFromJsonAsync(LocalBrowserApprovalJson.Default.LocalBrowserApprovalReply))!.Token;
        for (var i = 0; i < 4; i++) { time += (long)TimeSpan.FromMinutes(50).TotalMilliseconds; await Handshake(endpoint, renewedToken, Pages, true); }
        time += (long)TimeSpan.FromMinutes(50).TotalMilliseconds;
        await Handshake(endpoint, renewedToken, Pages, true);
        time += (long)LocalBrowserConnections.IdleLifetime.TotalMilliseconds + 1;
        await Handshake(endpoint, renewedToken, Pages, false);
        Wait();
        var fourth = pairing.Issue();
        using var capped = await Pair(fourth);
        var cappedToken = (await capped.Content.ReadFromJsonAsync(LocalBrowserApprovalJson.Default.LocalBrowserApprovalReply))!.Token;
        for (var step = 0; step < 14; step++) { time += (long)TimeSpan.FromMinutes(50).TotalMilliseconds; await Handshake(endpoint, cappedToken, Pages, true); }
        time += (long)TimeSpan.FromMinutes(50).TotalMilliseconds;
        await Handshake(endpoint, cappedToken, Pages, false);

        // The master token still works for clients that hold it.
        await Handshake(endpoint, "pairing-master", null, true);
        Console.WriteLine("PASS browser pairing: origin allow-list, Host check, private network preflight, wrong/used/expired/burned codes, cooldown, hashed credentials, origin binding, revocation, idle and absolute expiry");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}