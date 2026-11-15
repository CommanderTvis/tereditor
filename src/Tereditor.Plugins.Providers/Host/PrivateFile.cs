using System.Text;

namespace Tereditor.Plugins.Providers.Host;

/// <summary>Writes files only their owner can read. A secret is never in a file anyone else can open, not even for a moment.</summary>
internal static class PrivateFile
{
    /// <summary>
    /// Writes <paramref name="text"/> to a sibling file that exists, empty and readable by its owner only, before the secret is
    /// put into it, then moves it over <paramref name="path"/>. With <paramref name="privateDirectory"/> the parent directory is
    /// created readable by its owner only too.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="text">Its new content.</param>
    /// <param name="privateDirectory">Whether the parent directory is owner-only (0700).</param>
    public static void Write(string path, string text, bool privateDirectory = false)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (privateDirectory && !OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else Directory.CreateDirectory(directory);
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, []);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}