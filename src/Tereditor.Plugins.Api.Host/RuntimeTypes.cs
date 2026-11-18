namespace Tereditor.Plugins.Api.Host;

/// <summary>A program runtime the host can provide to plugins: Bun (<c>bunx</c>) or uv (<c>uvx</c>).</summary>
public enum RuntimeKind
{
    /// <summary>Bun. <see cref="RuntimeResolution.Args"/> start with <c>x</c>, so the command runs a package the way <c>bunx</c> does.</summary>
    Bun,

    /// <summary>uv. <see cref="RuntimeResolution.Args"/> start with <c>tool run</c>, so the command runs a package the way <c>uvx</c> does.</summary>
    Uv
}

/// <summary>Where a runtime came from, in the order the host tries them.</summary>
public enum RuntimeSource
{
    /// <summary>The path the user pinned in Settings › Runtimes for tooling.</summary>
    Pinned,

    /// <summary>An install found on the host's PATH, outside the workspace.</summary>
    System,

    /// <summary>The copy the Runtimes for tooling plugin downloaded into its cache.</summary>
    Downloaded
}

/// <summary>
/// The answer to a request for a runtime. When <see cref="Available"/>, run <see cref="Command"/> with <see cref="Args"/> then the package and its own
/// arguments, with <see cref="Env"/> over the plugin's own environment. Otherwise <see cref="Message"/> says in plain words what is missing.
/// </summary>
/// <param name="Kind">The runtime that was asked for.</param>
/// <param name="Available">Whether the runtime can be run now.</param>
/// <param name="Command">The absolute path of the program; empty when not available.</param>
/// <param name="Args">The arguments that go before the package: <c>x</c> for Bun, <c>tool run</c> for uv.</param>
/// <param name="Env">The environment to add: it keeps the runtime's cache in the host's cache and ignores configuration files in the working directory.</param>
/// <param name="Source">Where the program came from; null when not available.</param>
/// <param name="Message">Empty when available; otherwise the sentence to show the user.</param>
/// <param name="OfferEnable">True when the only thing missing is the Runtimes for tooling plugin: show <see cref="Message"/> with a way to turn it on.</param>
public sealed record RuntimeResolution(RuntimeKind Kind, bool Available, string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env,
    RuntimeSource? Source, string Message, bool OfferEnable)
{
    /// <summary>A runtime that cannot be provided.</summary>
    public static RuntimeResolution Unavailable(RuntimeKind kind, string message, bool offerEnable = false) =>
        new(kind, false, "", [], new Dictionary<string, string>(), null, message, offerEnable);
}

/// <summary>A download a plugin's request is about to start: which runtime, its name for the user and the pinned version.</summary>
public sealed record RuntimeOffer(RuntimeKind Kind, string Name, string Version);

/// <summary>What Settings › Runtimes for tooling shows for one runtime.</summary>
/// <param name="Kind">The runtime.</param>
/// <param name="Name">Its name for the user, <c>Bun</c> or <c>uv</c>.</param>
/// <param name="Source">The source in use now; null when none is available.</param>
/// <param name="Path">The program in use; empty when none.</param>
/// <param name="Version">Its version as it reports it; empty when unknown.</param>
/// <param name="Pinned">The path the user pinned; empty when none.</param>
/// <param name="DownloadBytes">The disk space of the downloaded copy and its caches.</param>
/// <param name="Downloaded">Whether a downloaded copy exists.</param>
/// <param name="Message">A plain sentence about the state, for example why nothing is available.</param>
public sealed record RuntimeStatus(RuntimeKind Kind, string Name, RuntimeSource? Source, string Path, string Version, string Pinned, long DownloadBytes, bool Downloaded, string Message)
{
    /// <summary>The program of the downloaded copy, whichever source is in use; empty when none is downloaded.</summary>
    public string DownloadedPath { get; init; } = "";
}

/// <summary>Removes what a plugin downloaded. Every plugin that downloads something removes all of it, and only that, when the user turns the plugin off.</summary>
public static class PluginDownloads
{
    /// <summary>
    /// Deletes each folder or file completely, including read-only files, without following links out of it. A path that does not exist is skipped.
    /// Never pass a folder that holds the user's own data (sign-ins, keys, configuration).
    /// </summary>
    /// <returns>The number of bytes freed.</returns>
    public static long Purge(IEnumerable<string> paths)
    {
        long freed = 0;
        foreach (var path in paths)
        {
            if (File.Exists(path)) { freed += Delete(new FileInfo(path)); continue; }
            if (!Directory.Exists(path)) continue;
            if (new DirectoryInfo(path).LinkTarget is not null) { Directory.Delete(path); continue; }
            freed += Size(path);
            try { Directory.Delete(path, true); }
            catch (UnauthorizedAccessException) { Unlock(path); Directory.Delete(path, true); }
        }
        return freed;
    }

    /// <summary>The bytes the folders hold (links are not followed).</summary>
    public static long Size(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
                if (entry is FileInfo file) total += file.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return total;
    }

    private static long Delete(FileInfo file)
    {
        var length = file.Length;
        file.Attributes = FileAttributes.Normal;
        file.Delete();
        return length;
    }

    private static void Unlock(string path)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
        {
            try { entry.Attributes = FileAttributes.Normal; if (!OperatingSystem.IsWindows() && entry is DirectoryInfo) File.SetUnixFileMode(entry.FullName, (UnixFileMode)0x1ff); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}