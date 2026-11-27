using System.Text;

namespace Tereditor.Plugins.Agent.Host;

/// <summary>A file that tereditor writes whole and owns.</summary>
internal static class OwnedFile
{
    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> when the file is missing or different, atomically, since several launches
    /// may share it. <paramref name="mode"/> sets the file's permissions where the system has them.
    /// </summary>
    public static void Write(string path, string text, UnixFileMode? mode = null)
    {
        try { if (File.Exists(path) && File.ReadAllText(path) == text) return; }
        catch (IOException) { }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        if (mode is { } permissions && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, permissions);
        File.Move(temporary, path, true);
    }
}