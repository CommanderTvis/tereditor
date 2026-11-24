using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tereditor.Host.Protocol;

/// <summary>A temporary, origin-bound browser credential issued after approval in the local app or a pairing code.</summary>
public sealed record LocalBrowserApprovalReply(string Token);

/// <summary>The body of a pairing request: the one-time code the host printed. A request without a body asks for approval in the app.</summary>
public sealed record LocalBrowserPairingRequest(string Code);

/// <summary>Public web origins, besides loopback ones, that may ask a local host for a credential.</summary>
public static class LocalBrowserOrigins
{
    /// <summary>The published browser client. Every site under this origin shares it, so a page there is trusted as much as the client.</summary>
    public const string Published = "https://commandertvis.github.io";

    public static bool IsPublished(string origin) => origin == Published;
}

/// <summary>The messages' source-generated JSON metadata, with the web defaults ASP.NET writes and System.Net.Http.Json reads.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LocalBrowserApprovalReply))]
[JsonSerializable(typeof(LocalBrowserPairingRequest))]
public sealed partial class LocalBrowserApprovalJson : JsonSerializerContext;