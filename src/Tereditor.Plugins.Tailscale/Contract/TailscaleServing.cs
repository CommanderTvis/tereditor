namespace Tereditor.Plugins.Tailscale;

/// <summary>A reason the host cannot serve a paired device, worded to be sent to that device.</summary>
public sealed class TailscaleServingException(string message) : IOException(message);

/// <summary>The builtin plugin's bridge to its owner's listener; it never owns the host itself.</summary>
public sealed class TailscaleServing
{
    public Func<string, CancellationToken, ValueTask<string>>? EnsureListening { get; set; }
    private Func<string, bool>? authenticate;
    public Func<string, bool>? Authenticate { get => Volatile.Read(ref authenticate); set => Volatile.Write(ref authenticate, value); }
    public Func<ValueTask>? StopListening { get; set; }
}