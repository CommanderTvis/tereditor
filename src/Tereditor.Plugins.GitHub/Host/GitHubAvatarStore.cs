namespace Tereditor.Plugins.GitHub.Host;

/// <summary>
/// Avatar images on disk, so a list shows its avatars at once, also offline and after a restart. One small file per normalized address
/// (<see cref="GitHubAvatarUrl"/>); at most <c>maxEntries</c> files and <c>maxBytes</c> together, least recently used first. Files are written
/// atomically with owner-only permissions. The host fetches (public https only, no credentials, 256 KB at most); the UI never does.
/// Asks for the same address while it is being fetched share one request. With the cache switched off an image is fetched and not saved.
/// </summary>
public sealed class GitHubAvatarStore(string directory, Func<bool> enabled, Func<Uri, CancellationToken, Task<byte[]?>>? fetch = null, int maxEntries = 500, long maxBytes = 8L << 20)
{
    public const int MaxImageBytes = 256 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Func<Uri, CancellationToken, Task<byte[]?>> download = fetch ?? Download;
    private readonly Dictionary<string, Task<byte[]?>> flights = [];
    private readonly object gate = new();
    private bool secured;

    public Task<byte[]?> GetAsync(string url, CancellationToken ct)
    {
        if (GitHubAvatarUrl.Normalize(url) is not { } uri) return Task.FromResult<byte[]?>(null);
        var key = uri.AbsoluteUri;
        Task<byte[]?> flight;
        lock (flights)
        {
            if (!flights.TryGetValue(key, out flight!))
            {
                flight = flights[key] = Task.Run(() => LoadAsync(uri, key));
                _ = flight.ContinueWith(_ => { lock (flights) flights.Remove(key); }, TaskScheduler.Default);
            }
        }
        return flight.WaitAsync(ct);
    }

    /// <summary>Bytes the saved images take on disk.</summary>
    public Task<long> SizeAsync() => Task.Run(Size);

    /// <summary>Deletes every saved image; returns the bytes freed.</summary>
    public Task<long> ClearAsync() => Task.Run(Clear);

    private async Task<byte[]?> LoadAsync(Uri uri, string key)
    {
        var path = PathOf(key);
        lock (gate)
        {
            if (enabled() && ReadFile(path) is { } saved) return saved;
        }
        byte[]? bytes;
        try { bytes = await download(uri, CancellationToken.None); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { return null; }
        if (bytes is null || !IsImage(bytes)) return null;
        lock (gate)
        {
            if (enabled()) WriteFile(path, bytes);
        }
        return bytes;
    }

    private string PathOf(string key) => Path.Combine(directory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + ".img");

    private static byte[]? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (!IsImage(bytes)) { TryDelete(path); return null; }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private void WriteFile(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Secure();
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options)) stream.Write(bytes);
            File.Move(temp, path, true);
            Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { TryDelete(temp); }
    }

    /// <summary>Drops the least recently used images until both bounds hold.</summary>
    private void Trim()
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("*.img").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        var left = files.Count;
        foreach (var file in files)
        {
            if (left <= maxEntries && total <= maxBytes) break;
            TryDelete(file.FullName);
            left--;
            total -= file.Length;
        }
    }

    private void Secure()
    {
        if (secured && Directory.Exists(directory)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, mode);
            File.SetUnixFileMode(directory, mode);
        }
        secured = true;
    }

    private long Size()
    {
        lock (gate)
        {
            try { return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.img").Sum(p => new FileInfo(p).Length) : 0; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
        }
    }

    private long Clear()
    {
        lock (gate)
        {
            var freed = Size();
            try
            {
                if (Directory.Exists(directory))
                    foreach (var path in Directory.EnumerateFiles(directory)) TryDelete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return freed;
        }
    }

    /// <summary>PNG, JPEG, GIF or WebP, by the first bytes; anything else is never saved or served.</summary>
    internal static bool IsImage(byte[] b) =>
        b.Length is > 12 and <= MaxImageBytes && (b is [0x89, 0x50, 0x4E, 0x47, ..] || b is [0xFF, 0xD8, 0xFF, ..] || b is [0x47, 0x49, 0x46, 0x38, ..]
            || b is [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..]);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<byte[]?> Download(Uri uri, CancellationToken ct)
    {
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + count > MaxImageBytes) return null;
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
}