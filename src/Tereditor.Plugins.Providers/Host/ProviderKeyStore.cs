using System.Text;

namespace Tereditor.Plugins.Providers.Host;

/// <summary>
/// The one place model provider keys live: <c>&lt;state&gt;/providers/keys/&lt;id&gt;</c>, a directory only the owner can enter holding
/// one file per provider, each readable by its owner only and replaced whole through a temporary file. A key goes in once and is
/// never read back by tereditor: an agent gets it from the <c>provider-key</c> helper, which prints it to its caller's standard output.
/// </summary>
internal sealed class ProviderKeyStore(string root)
{
    /// <summary>The state directory: <c>TEREDITOR_STATE_DIR</c>, else <c>~/.tereditor</c>.</summary>
    public static string DefaultRoot() =>
        Environment.GetEnvironmentVariable("TEREDITOR_STATE_DIR") is { Length: > 0 } configured ? configured
        : Path.Combine(Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor");

    public string KeysDirectory => Path.Combine(root, "providers", "keys");

    public string BinDirectory => Path.Combine(root, "providers", "bin");

    /// <summary>The helper program an agent runs with the provider id as its argument.</summary>
    public string HelperPath => ProviderKeyHelper.Path(BinDirectory);

    public bool Has(string id) => ProviderCatalog.IsProviderId(id) && File.Exists(Path.Combine(KeysDirectory, id));

    /// <summary>Stores <paramref name="key"/> for <paramref name="id"/>, and makes sure the helper exists.</summary>
    public void Save(string id, string key)
    {
        if (!ProviderCatalog.IsProviderId(id)) throw new ArgumentException("A provider id uses lower case letters, digits, dots, dashes and underscores.");
        key = key.Trim();
        if (key.Length == 0) throw new ArgumentException("A key cannot be empty.");
        if (key.Any(c => c is '\n' or '\r' or '\0')) throw new ArgumentException("A key is one line.");
        PrivateFile.Write(Path.Combine(KeysDirectory, id), key, privateDirectory: true);
        ProviderKeyHelper.Ensure(KeysDirectory, BinDirectory);
    }

    public void Remove(string id)
    {
        if (!ProviderCatalog.IsProviderId(id)) return;
        var path = Path.Combine(KeysDirectory, id);
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// The <c>provider-key &lt;id&gt;</c> program: it prints the stored key of a provider and nothing else, so an agent's own
/// credential-helper setting can call it (Claude Code <c>apiKeyHelper</c>, Codex <c>auth.command</c>, Pi and omp <c>!command</c>).
/// The host writes it, with the keys directory built in, when a key is first stored; a POSIX shell script and a Windows
/// <c>.cmd</c>, so it needs nothing shipped with the app and works in the NativeAOT host and the headless archive alike.
/// It refuses an id that is not a plain provider id, so it cannot be pointed at another file.
/// </summary>
internal static class ProviderKeyHelper
{
    public const string Name = "provider-key";

    public static string Path(string binDirectory) => System.IO.Path.Combine(binDirectory, OperatingSystem.IsWindows() ? Name + ".cmd" : Name);

    /// <summary>The words an agent runs to get the key of <paramref name="id"/>: the helper and the id.</summary>
    public static IReadOnlyList<string> Words(string binDirectory, string id) => [Path(binDirectory), id];

    public static string Script(string keysDirectory) =>
        "#!/bin/sh\n# Prints the stored key of a model provider. Written by tereditor; changes are overwritten.\n" +
        "case \"$1\" in ''|.*|*[!a-z0-9._-]*) echo 'provider-key: not a provider id' >&2; exit 2;; esac\n" +
        "exec cat -- '" + keysDirectory.Replace("'", "'\\''", StringComparison.Ordinal) + "'/\"$1\"\n";

    public static string CmdScript(string keysDirectory) =>
        "@echo off\r\nrem Prints the stored key of a model provider. Written by tereditor; changes are overwritten.\r\n" +
        "if \"%~1\"==\"\" exit /b 2\r\n" +
        "echo %~1| findstr /r /c:\"^[a-z0-9_-][a-z0-9._-]*$\" >nul || exit /b 2\r\n" +
        "type \"" + keysDirectory + "\\%~1\"\r\n";

    /// <summary>Writes both scripts when they are missing or stale.</summary>
    public static void Ensure(string keysDirectory, string binDirectory)
    {
        Write(System.IO.Path.Combine(binDirectory, Name), Script(keysDirectory), executable: true);
        Write(System.IO.Path.Combine(binDirectory, Name + ".cmd"), CmdScript(keysDirectory), executable: false);
    }

    private static void Write(string file, string content, bool executable)
    {
        if (File.Exists(file) && File.ReadAllText(file) == content) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var temporary = file + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, executable ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
