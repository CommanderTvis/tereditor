using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tereditor.Plugins.GitHub.Host;

/// <summary>
/// Stale while revalidate. A request that holds no revision (a tab just opened) gets the saved answer at once, marked as
/// refreshing, while one gh call runs in the background and saves its result. The tab asks again; that call waits for the same
/// gh call and gets the fresh answer, or <c>Unchanged</c> when it matches what was shown. A failed gh call with a saved answer
/// returns the saved answer with the reason. Without a saved answer, or with the cache off, the answer is the live one.
/// </summary>
public sealed partial class GitHubBrowser
{
    private static readonly TimeSpan FlightKeep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AccountTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FallbackAccountTtl = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (Task Task, long At)> flights = new();
    private sealed record Account(string? Login, long At, TimeSpan Ttl);

    private Account? account;
    private readonly ConcurrentDictionary<string, (string? Slug, long At)> remotes = new();

    private sealed record Fetched<T>(T Full, GitHubCacheHit? Saved);

    private sealed record Codec<T>(Func<T, string?> Problem, Func<T, string> Revision, Func<T, bool> Cacheable, Func<T, string?, T> Trim, Func<T, GitHubCacheNote, T> Note,
        Func<T, string> Write, Func<string, T?> Read) where T : class;

    private static readonly Codec<GitHubList> ListCodec = new(l => l.Problem, l => l.Revision, _ => true,
        (l, have) => have == l.Revision ? l with { Unchanged = true, Items = [] } : l, (l, note) => l with { Cached = note },
        l => JsonSerializer.Serialize(l, GitHubJson.Default.GitHubList), s => Parse(s, GitHubJson.Default.GitHubList));

    private static readonly Codec<GitHubView> ViewCodec = new(v => v.Problem, v => v.Revision, _ => true,
        (v, have) => have == v.Revision ? v with { Unchanged = true, Rows = [], Cards = [], Runs = [], Workflows = [] } : v, (v, note) => v with { Cached = note },
        v => JsonSerializer.Serialize(v, GitHubJson.Default.GitHubView), s => Parse(s, GitHubJson.Default.GitHubView));

    private static readonly Codec<GitHubDetail> DetailCodec = new(d => d.Problem, d => d.Revision, d => d.TimelineProblem is null,
        (d, have) => have == d.Revision ? d with { Unchanged = true, Body = "", Timeline = [] } : d, (d, note) => d with { Cached = note },
        d => JsonSerializer.Serialize(d, GitHubJson.Default.GitHubDetail), s => Parse(s, GitHubJson.Default.GitHubDetail));

    private static T? Parse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try { return JsonSerializer.Deserialize(json, info); }
        catch (JsonException) { return null; }
    }

    public Task<GitHubList> ListAsync(string directory, GitHubListParams p, CancellationToken ct) =>
        ServeAsync(directory, "list:" + p.Kind, p.Query.Trim() + "|" + Math.Clamp(p.Limit, 1, 100).ToString(CultureInfo.InvariantCulture), p.Revision, ListCodec,
            token => FetchListAsync(directory, p with { Revision = null }, token), ct);

    public Task<GitHubDetail> DetailAsync(string directory, GitHubDetailParams p, CancellationToken ct) =>
        ServeAsync(directory, "detail:" + p.Kind, p.Number.ToString(CultureInfo.InvariantCulture), p.Revision, DetailCodec,
            token => FetchDetailAsync(directory, p with { Revision = null }, token), ct);

    public Task<GitHubView> ViewAsync(string directory, GitHubViewParams p, CancellationToken ct) =>
        ServeAsync(directory, "view:" + p.Kind, p.Id + "|" + string.Join(',', p.Filters.Where(f => f.Value.Length > 0).GroupBy(f => f.Key).Select(g => g.Key + "=" + g.Last().Value).Order(StringComparer.Ordinal))
            + "|" + Math.Clamp(p.Limit, 1, 100).ToString(CultureInfo.InvariantCulture) + "|" + Math.Max(1, p.Page).ToString(CultureInfo.InvariantCulture), p.Revision, ViewCodec, token => FetchViewAsync(directory, p with { Revision = null }, token), ct);

    /// <summary>Bytes the local copy takes on disk.</summary>
    public Task<long> CacheSizeAsync() => cache is null ? Task.FromResult(0L) : cache.SizeAsync();

    /// <summary>Deletes the local copy; returns the bytes freed. Works while the cache is switched off.</summary>
    public Task<long> ClearCacheAsync() => cache is null ? Task.FromResult(0L) : cache.ClearAsync();

    private async Task<T> ServeAsync<T>(string directory, string group, string key, string? have, Codec<T> codec, Func<CancellationToken, Task<T>> fetch, CancellationToken ct) where T : class
    {
        if (cache is not { Enabled: true }) return codec.Trim(await fetch(ct), have);
        var scope = await ScopeAsync(directory, ct);
        if (scope is null) return codec.Trim(await fetch(ct), have);
        var (login, slug) = scope.Value;
        var flightKey = login + "|" + slug + "|" + group + "|" + key;
        var running = flights.TryGetValue(flightKey, out var existing) && Environment.TickCount64 - existing.At < FlightKeep.TotalMilliseconds;
        if (string.IsNullOrEmpty(have) && await cache.ReadAsync(login, slug, group, key) is { } hit && codec.Read(hit.Payload) is { } saved)
        {
            if (!running) _ = Start();
            return codec.Note(saved, new(true, hit.FetchedAt.ToString("o", CultureInfo.InvariantCulture)));
        }
        var flight = running ? (Task<Fetched<T>>)existing.Task : Start();
        var fetched = await flight.WaitAsync(ct);
        if (fetched.Saved is { } stale && codec.Read(stale.Payload) is { } copy)
            return codec.Note(codec.Trim(copy, have), new(false, stale.FetchedAt.ToString("o", CultureInfo.InvariantCulture), codec.Problem(fetched.Full)));
        return codec.Trim(fetched.Full, have);

        Task<Fetched<T>> Start()
        {
            var task = Task.Run(async () =>
            {
                var full = await fetch(CancellationToken.None);
                if (codec.Problem(full) is null)
                {
                    if (codec.Cacheable(full)) await cache.WriteAsync(login, slug, group, key, codec.Write(full), codec.Revision(full));
                    return new Fetched<T>(full, null);
                }
                return new Fetched<T>(full, await cache.ReadAsync(login, slug, group, key));
            });
            foreach (var old in flights.Where(f => Environment.TickCount64 - f.Value.At >= FlightKeep.TotalMilliseconds).Select(f => f.Key)) flights.TryRemove(old, out _);
            flights[flightKey] = (task, Environment.TickCount64);
            _ = task.ContinueWith(t => { flights.TryRemove(flightKey, out _); return t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return task;
        }
    }

    /// <summary>The gh login and the repository of this folder's origin, or null when either is unknown (then nothing is cached).</summary>
    private async Task<(string Login, string Slug)?> ScopeAsync(string directory, CancellationToken ct)
    {
        try
        {
            var login = await LoginAsync(directory, ct);
            var slug = await RemoteSlugAsync(directory, ct);
            return login is null || slug is null ? null : (login, slug);
        }
        catch (Exception error) when (error is IOException or TimeoutException or Win32Exception) { return null; }
    }

    /// <summary>The signed-in login (gh api user); offline, the active account that gh auth status lists, so the saved copy still opens.</summary>
    private async Task<string?> LoginAsync(string directory, CancellationToken ct)
    {
        var current = account;
        if (current is { Login: not null } && Environment.TickCount64 - current.At < current.Ttl.TotalMilliseconds) return current.Login;
        string? login = null;
        var ttl = AccountTtl;
        try
        {
            var result = await run("gh", directory, ["api", "user", "--jq", ".login"], TimeSpan.FromSeconds(6), GhEnvironment, ct);
            if (result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } name) login = name;
        }
        catch (Exception error) when (error is TimeoutException or IOException) { }
        if (login is null)
        {
            ttl = FallbackAccountTtl;
            var status = await run("gh", directory, ["auth", "status"], TimeSpan.FromSeconds(8), GhEnvironment, ct);
            login = ActiveAccount(status.Output + "\n" + status.Error);
        }
        account = new(login, Environment.TickCount64, ttl);
        return login;
    }

    /// <summary>The account gh marks active (or the only one) in the text of gh auth status.</summary>
    internal static string? ActiveAccount(string text)
    {
        string? last = null, first = null;
        foreach (var line in text.Split('\n'))
        {
            if (AccountLine().Match(line) is { Success: true } match) { last = match.Groups[1].Value; first ??= last; }
            else if (last is not null && line.Contains("Active account: true", StringComparison.Ordinal)) return last;
        }
        return first;
    }

    private async Task<string?> RemoteSlugAsync(string directory, CancellationToken ct)
    {
        if (remotes.TryGetValue(directory, out var known) && Environment.TickCount64 - known.At < RepoTtl.TotalMilliseconds) return known.Slug;
        var result = await run("git", directory, ["remote", "get-url", "origin"], TimeSpan.FromSeconds(5), GhEnvironment, ct);
        var slug = result.ExitCode == 0 && RemoteUrl().Match(result.Output.Trim()) is { Success: true } m ? (m.Groups[1].Value + "/" + m.Groups[2].Value).ToLowerInvariant() : null;
        remotes[directory] = (slug, Environment.TickCount64);
        return slug;
    }

    [GeneratedRegex(@"account (\S+)")]
    private static partial Regex AccountLine();

    [GeneratedRegex(@"github\.com[:/]+([^/\s]+)/([^/\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteUrl();
}