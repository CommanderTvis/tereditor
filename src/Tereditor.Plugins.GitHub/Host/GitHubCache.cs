using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tereditor.Plugins.GitHub.Host;

internal sealed record GitHubCacheEntry(string Key, string Payload, string Revision, DateTimeOffset FetchedAt);

internal sealed record GitHubCacheFile(int Version, string Account, string Repo, string Group, List<GitHubCacheEntry> Entries);

[JsonSerializable(typeof(GitHubCacheFile))]
internal sealed partial class GitHubCacheJson : JsonSerializerContext;

/// <summary>A saved answer: the parsed contract record as JSON, its revision and when gh returned it.</summary>
public sealed record GitHubCacheHit(string Payload, string Revision, DateTimeOffset FetchedAt);

/// <summary>
/// The local copy of GitHub answers, so a tab can show the last answer at once. One small JSON file per gh login, repository and
/// kind of view, under the plugin's own state directory. A file holds at most <c>maxEntries</c> requests (oldest fetch goes first),
/// nothing older than <c>maxAge</c>, and all files together stay under <c>maxBytes</c> (oldest entries go first). Files are written
/// atomically with owner-only permissions. A file that does not parse or has another format version is deleted, never migrated.
/// </summary>
public sealed class GitHubCache(string directory, Func<bool> enabled, int maxEntries = 200, long maxBytes = 20L << 20, TimeSpan? maxAge = null, Func<DateTimeOffset>? clock = null)
{
    public const int FormatVersion = 2;
    private readonly TimeSpan age = maxAge ?? TimeSpan.FromDays(30);
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object gate = new();
    private bool secured;

    public bool Enabled => enabled();

    public Task<GitHubCacheHit?> ReadAsync(string account, string repo, string group, string key) => Task.Run(() => Read(account, repo, group, key));

    public Task WriteAsync(string account, string repo, string group, string key, string payload, string revision) =>
        Task.Run(() => Write(account, repo, group, key, payload, revision));

    /// <summary>Bytes the cache files take on disk.</summary>
    public Task<long> SizeAsync() => Task.Run(Size);

    /// <summary>Deletes every cache file; returns the bytes freed.</summary>
    public Task<long> ClearAsync() => Task.Run(Clear);

    private static string Name(string account, string repo, string group) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.ToLowerInvariant() + "\0" + repo.ToLowerInvariant() + "\0" + group)))[..32].ToLowerInvariant() + ".json";

    private GitHubCacheHit? Read(string account, string repo, string group, string key)
    {
        lock (gate)
        {
            if (!enabled()) return null;
            var file = Load(Path.Combine(directory, Name(account, repo, group)), account, repo, group);
            var entry = file?.Entries.FirstOrDefault(e => e.Key == key && now() - e.FetchedAt < age);
            return entry is null ? null : new(entry.Payload, entry.Revision, entry.FetchedAt);
        }
    }

    private void Write(string account, string repo, string group, string key, string payload, string revision)
    {
        lock (gate)
        {
            if (!enabled()) return;
            try
            {
                Secure();
                var path = Path.Combine(directory, Name(account, repo, group));
                var file = Load(path, account, repo, group) ?? new(FormatVersion, account.ToLowerInvariant(), repo.ToLowerInvariant(), group, []);
                file.Entries.RemoveAll(e => e.Key == key || now() - e.FetchedAt >= age);
                file.Entries.Add(new(key, payload, revision, now()));
                if (file.Entries.Count > maxEntries) file.Entries.RemoveRange(0, file.Entries.Count - maxEntries);
                Save(path, file);
                Trim();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private GitHubCacheFile? Load(string path, string account, string repo, string group)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), GitHubCacheJson.Default.GitHubCacheFile);
            if (file is { Version: FormatVersion, Entries: not null } && file.Account == account.ToLowerInvariant() && file.Repo == repo.ToLowerInvariant() && file.Group == group) return file;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
        TryDelete(path);
        return null;
    }

    private void Save(string path, GitHubCacheFile file)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(file, GitHubCacheJson.Default.GitHubCacheFile));
            File.Move(temp, path, true);
        }
        finally { TryDelete(temp); }
    }

    /// <summary>Creates the directory owner-only (0700 on Unix) and tightens one that already exists.</summary>
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

    /// <summary>Keeps all files together under the size cap by dropping the oldest entries first.</summary>
    private void Trim()
    {
        var paths = Directory.EnumerateFiles(directory, "*.json").ToList();
        if (paths.Sum(p => new FileInfo(p).Length) <= maxBytes) return;
        var files = new Dictionary<string, GitHubCacheFile>();
        foreach (var path in paths)
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllBytes(path), GitHubCacheJson.Default.GitHubCacheFile) is { Version: FormatVersion, Entries: not null } file) files[path] = file;
                else TryDelete(path);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { TryDelete(path); }
        }
        static long Weight(GitHubCacheEntry e) => e.Payload.Length + e.Key.Length + e.Revision.Length + 96;
        var oldest = files.SelectMany(f => f.Value.Entries.Select(e => (File: f.Key, Entry: e))).OrderBy(x => x.Entry.FetchedAt).ToList();
        var total = oldest.Sum(x => Weight(x.Entry));
        var touched = new HashSet<string>();
        foreach (var (path, entry) in oldest)
        {
            if (total <= maxBytes) break;
            files[path].Entries.Remove(entry);
            total -= Weight(entry);
            touched.Add(path);
        }
        foreach (var path in touched)
            if (files[path].Entries.Count == 0) TryDelete(path); else Save(path, files[path]);
    }

    private long Size()
    {
        lock (gate)
        {
            try { return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Sum(p => new FileInfo(p).Length) : 0; }
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}