using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Tereditor.Plugins.Tailscale.Host;

internal sealed record ApprovedDevice(string Hash, string NodeId, string Name, string User);
internal sealed record PendingDevice(ApprovedDevice Device, DateTimeOffset Expires, bool Rejected = false);

internal sealed class PairingRegistry(IReadOnlyList<ApprovedDevice> restored, Action<IReadOnlyList<ApprovedDevice>> save, Func<DateTimeOffset>? clock = null)
{
    private readonly object gate = new();
    private readonly Dictionary<string, ApprovedDevice> approved = restored.GroupBy(device => device.Hash).ToDictionary(group => group.Key, group => group.First());
    private readonly Dictionary<string, PendingDevice> pending = [];
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    internal static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    internal static bool ValidToken(string token) => token.Length == 64 && token.All(char.IsAsciiHexDigit);

    internal string Request(string token, string nodeId, string name, string user)
    {
        if (!ValidToken(token)) return "invalid";
        lock (gate)
        {
            Expire();
            var hash = Hash(token);
            if (approved.TryGetValue(hash, out var known)) return known.NodeId == nodeId ? "approved" : "denied";
            if (pending.TryGetValue(hash, out var waiting))
                return waiting.Device.NodeId != nodeId || waiting.Rejected ? "denied" : "pending";
            if (pending.Count >= 32) return "busy";
            pending.Add(hash, new(new(hash, nodeId, name, user), Now.AddMinutes(2)));
            return "pending";
        }
    }

    internal bool Authenticate(string token)
    {
        if (!ValidToken(token)) return false;
        lock (gate) return approved.ContainsKey(Hash(token));
    }

    internal void Decide(string id, bool allow)
    {
        lock (gate)
        {
            Expire();
            if (!pending.TryGetValue(id, out var entry) || entry.Rejected) throw new InvalidOperationException("This pairing request has expired.");
            if (allow)
            {
                // A known device that came back with a new node replaces its old record; a changed key was still approved by the user above.
                var superseded = Superseded(entry.Device).Select(device => device.Hash).ToHashSet();
                if (approved.Count - superseded.Count >= 128) throw new InvalidOperationException("Remove a remembered device before approving another.");
                var next = approved.Values.Where(device => !superseded.Contains(device.Hash)).Append(entry.Device).ToArray();
                save(next);
                foreach (var hash in superseded) approved.Remove(hash);
                approved.Add(id, entry.Device);
                pending.Remove(id);
            }
            else pending[id] = entry with { Rejected = true };
        }
    }

    internal void Revoke(string id)
    {
        lock (gate)
        {
            if (!approved.TryGetValue(id, out var revoked)) return;
            var next = approved.Values.Where(device => device.NodeId != revoked.NodeId).ToArray();
            save(next);
            foreach (var key in approved.Values.Where(device => device.NodeId == revoked.NodeId).Select(device => device.Hash).ToArray()) approved.Remove(key);
        }
    }

    internal TailscaleStatus Status(string detail, string? hostName)
    {
        lock (gate)
        {
            Expire();
            return new(detail, hostName, pending.Values.Where(entry => !entry.Rejected).Select(entry => Display(entry.Device, Note(entry.Device))).ToArray(), approved.Values.GroupBy(device => device.NodeId).Select(group => Display(group.First())).ToArray());
        }
    }

    private static TailscaleDevice Display(ApprovedDevice device, string? note = null) => new(device.Hash, device.Name, device.User, device.Hash[..8].ToUpperInvariant(), note);

    /// <summary>The device name without Tailscale's automatic "-N" suffix, which it adds when a name is taken, for example after a node is re-created.</summary>
    internal static string StableName(string name)
    {
        var dot = name.IndexOf('.');
        var label = dot < 0 ? name : name[..dot];
        var dash = label.LastIndexOf('-');
        if (dash > 0 && dash < label.Length - 1 && label[(dash + 1)..].All(char.IsAsciiDigit)) label = label[..dash];
        return (dot < 0 ? label : label + name[dot..]).ToLowerInvariant();
    }

    private static bool SameDevice(ApprovedDevice a, ApprovedDevice b) =>
        a.User.Length > 0 && string.Equals(a.User, b.User, StringComparison.OrdinalIgnoreCase) && StableName(a.Name) == StableName(b.Name);

    /// <summary>Remembered devices of the same user and device name on another node: the new request is their successor, never trusted until approved.</summary>
    private ApprovedDevice[] Superseded(ApprovedDevice device) => approved.Values.Where(known => known.NodeId != device.NodeId && SameDevice(known, device)).ToArray();

    private string? Note(ApprovedDevice device)
    {
        if (Superseded(device) is { Length: > 0 } old) return "Known device with a new key. Approving replaces the remembered " + old[0].Name + ".";
        return approved.Values.Any(known => known.NodeId == device.NodeId && SameDevice(known, device)) ? "Known device with a new key." : null;
    }

    /// <summary>Drops expired requests; reports whether one the user could still answer went, so the host republishes.</summary>
    internal bool Sweep()
    {
        lock (gate)
        {
            var answerable = pending.Values.Count(entry => !entry.Rejected);
            Expire();
            return pending.Values.Count(entry => !entry.Rejected) != answerable;
        }
    }

    private void Expire()
    {
        foreach (var key in pending.Where(entry => entry.Value.Expires <= Now).Select(entry => entry.Key).ToArray()) pending.Remove(key);
    }
}

[JsonSerializable(typeof(ApprovedDevice[]))]
[JsonSerializable(typeof(IReadOnlyList<ApprovedDevice>))]
internal sealed partial class TailscaleHostJson : JsonSerializerContext;