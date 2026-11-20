namespace Tereditor.UI;

/// <summary>
/// The text of a worktree's Remove item: the size the host measured, a placeholder of the same shape while it measures,
/// and the plain label when the measurement fails. A size is kept for a short time, so reopening the menu shows it at once.
/// Used on the UI thread only; the host does the measuring.
/// </summary>
internal sealed class RemoveSizeLabels(Func<string, CancellationToken, ValueTask<long?>> measure, Func<long>? clock = null)
{
    internal const string Plain = "Remove worktree…", Measuring = "Remove worktree (measuring)…";
    internal const long Lifetime = 30_000;

    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private readonly Dictionary<string, (long Bytes, long At)> known = [];

    internal static string Text(long bytes) => $"Remove worktree ({WorkbenchWindow.WorkspaceSizeText(bytes)})…";

    private long? Cached(string path) => known.TryGetValue(path, out var kept) && now() - kept.At < Lifetime ? kept.Bytes : null;

    /// <summary>Sets the label now and again when the host answers. A cancelled token (the menu closed or moved on) leaves it untouched.</summary>
    internal async Task ShowAsync(Action<string> set, string path, CancellationToken token)
    {
        if (Cached(path) is { } cached) { set(Text(cached)); return; }
        set(Measuring);
        try
        {
            if (await measure(path, token) is { } bytes && !token.IsCancellationRequested)
            {
                known[path] = (bytes, now());
                set(Text(bytes));
                return;
            }
        }
        // The size only decorates the item.
        catch (Exception error) when (error is not OutOfMemoryException) { }
        if (!token.IsCancellationRequested) set(Plain);
    }

    /// <summary>Starts the measurement before the menu opens. The answer is kept, but only while the token lives, so a pointer that left does not start work.</summary>
    internal async Task PrefetchAsync(string path, TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (Cached(path) is not null) return;
            await Task.Delay(delay, token);
            if (await measure(path, token) is { } bytes && !token.IsCancellationRequested) known[path] = (bytes, now());
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    /// <summary>The worktree is going away or changed: the next ask measures again.</summary>
    internal void Forget(string path) => known.Remove(path);
}