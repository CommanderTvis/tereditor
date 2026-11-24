using System.Security.Cryptography;
using System.Text;

namespace Tereditor.Host.Remote;

/// <summary>
/// One live pairing code at a time: 10 characters of a 32-letter alphabet (50 bits), valid for minutes, redeemable once and
/// burned after a few wrong tries. Only the code's SHA-256 is kept. The owner prints the code on the host's terminal.
/// </summary>
public sealed class PairingCodes(TimeSpan? lifetime = null, int attempts = 5, Func<long>? clock = null)
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private readonly object sync = new();
    private readonly TimeSpan lifespan = lifetime ?? TimeSpan.FromMinutes(5);
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private byte[]? hash;
    private long expires;
    private int left;

    /// <summary>Raised with the new code after a use or a burn replaced it, so the owner can show it again.</summary>
    public event Action<string>? Reissued;

    /// <summary>Makes a new code and forgets the previous one.</summary>
    public string Issue() => Create();

    private string Create()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        var code = new StringBuilder(11);
        for (var i = 0; i < 10; i++)
        {
            if (i == 5) code.Append('-');
            code.Append(Alphabet[random[i] & 31]);
        }
        var text = code.ToString();
        lock (sync) { hash = Hash(text); expires = now() + (long)lifespan.TotalMilliseconds; left = attempts; }
        return text;
    }

    /// <summary>True once for the live code. A wrong, expired or used code is false and never says which.</summary>
    public bool Redeem(string? candidate)
    {
        var burned = false;
        bool ok;
        lock (sync)
        {
            var live = hash is not null && now() < expires && left > 0;
            var match = CryptographicOperations.FixedTimeEquals(Hash(candidate ?? ""), hash ?? new byte[32]);
            ok = live && match;
            if (ok) { hash = null; burned = true; }
            else if (live && --left == 0) { hash = null; burned = true; }
        }
        if (burned) Reissued?.Invoke(Create());
        return ok;
    }

    /// <summary>The hash of a code the way it is kept, whatever the case, dashes or spaces the user typed.</summary>
    internal static byte[] Hash(string code) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(new string([.. code.Where(c => c is not ('-' or ' '))]).ToUpperInvariant()));
}