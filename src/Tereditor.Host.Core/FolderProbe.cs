using System.Collections.Concurrent;

namespace Tereditor.Host.Core;

/// <summary>
/// Whether a project or workspace folder is there, without exceptions. A folder found missing is remembered for
/// <see cref="Remember"/>, so the many reads of the state projection cost no file system call for it; a fresh probe,
/// opening the folder or <see cref="Forget"/> look again at once.
/// </summary>
internal static class FolderProbe
{
    internal static readonly TimeSpan Remember = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<string, long> Missing = new(StringComparer.Ordinal);

    /// <summary>How many times a folder was really looked at, for checks that assert a missing folder is not polled.</summary>
    internal static long Probes;

    internal static bool Exists(string path, bool fresh = false)
    {
        if (!fresh && Missing.TryGetValue(path, out var seen) && Environment.TickCount64 - seen < Remember.TotalMilliseconds) return false;
        Interlocked.Increment(ref Probes);
        if (Directory.Exists(path))
        {
            Missing.TryRemove(path, out _);
            return true;
        }
        Missing[path] = Environment.TickCount64;
        return false;
    }

    /// <summary>Whether the folder is already remembered as missing, so only its first detection is worth a broadcast.</summary>
    internal static bool KnownMissing(string path) => Missing.ContainsKey(path);

    internal static void Forget(string path) => Missing.TryRemove(path, out _);
}