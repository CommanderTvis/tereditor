using System.Text.Json.Nodes;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>
/// The agent's own sign-in through the protocol: <c>initialize</c>, then <c>authenticate</c> with the <c>methodId</c> of a method the agent offered
/// (https://agentclientprotocol.com/protocol/authentication). The agent completes it itself, usually in the browser; no secret passes here.
/// A terminal login is refused: the protocol says the client must not send it to <c>authenticate</c>; it runs in a terminal instead.
/// </summary>
internal static class AcpAuthenticate
{
    /// <summary>A browser sign-in takes as long as the user does.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    public static async Task RunAsync(IAcpTransport owner, string methodId, CancellationToken cancellationToken)
    {
        await using var client = new AcpClient(owner.FromAgent, owner.ToAgent, new Refusing());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Limit);
        var info = await client.InitializeAsync("tereditor", "0.1", limit.Token);
        var method = info.AuthMethods.FirstOrDefault(offered => offered.Id == methodId) ?? throw new InvalidOperationException("The agent does not offer that sign-in.");
        if (method.Terminal) throw new InvalidOperationException("That sign-in runs in a terminal.");
        await client.AuthenticateAsync(methodId, limit.Token);
    }

    /// <summary>The client of a start that only signs in: the agent has no session, so it may not read files or run anything.</summary>
    private sealed class Refusing : IAcpClientHandler
    {
        public void Update(string sessionId, JsonObject update) { }

        public Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken) =>
            throw new AcpException(AcpException.MethodNotFound, "Not available while signing in.");
    }
}