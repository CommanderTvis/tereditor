namespace Tereditor.Plugins.Api;

/// <summary>Whether an agent's program was found. <see cref="Installed"/> is <see langword="null"/> when the probe could not decide.</summary>
/// <param name="Installed"><see langword="true"/> found, <see langword="false"/> not found, <see langword="null"/> undecided.</param>
/// <param name="Command">The command that was looked for.</param>
public sealed record AgentInstall(bool? Installed, string Command)
{
    /// <summary>A plain sentence for a launcher that is not installed, or <see langword="null"/> when it is or may be.</summary>
    /// <param name="product">The agent's name, for example <c>Claude Code</c>.</param>
    public string? Reason(string product) =>
        Installed == false ? $"{Command} was not found on PATH. Install {product}, then try again." : null;
}

/// <summary>Looks for an agent's program the way a terminal would, without starting anything.</summary>
public static class AgentExecutable
{
    /// <summary>
    /// Looks for <paramref name="command"/> in the files of <paramref name="path"/> (the process <c>PATH</c> when
    /// <see langword="null"/>), or at its own path when it names a directory. A command line with arguments, quotes or
    /// a shell word is undecided, and so is an empty <c>PATH</c>: the answer is then <see langword="null"/>, never
    /// <see langword="false"/>. Only file existence is read, so it is cheap, but it is file I/O: call it off the UI thread.
    /// </summary>
    /// <param name="command">The configured command.</param>
    /// <param name="path">A <c>PATH</c> value to search, or <see langword="null"/> for this process's.</param>
    /// <param name="windows">Whether program names take an executable extension.</param>
    public static AgentInstall Probe(string command, string? path = null, bool? windows = null)
    {
        var word = command.Trim();
        if (word.Length == 0 || word.Any(character => char.IsWhiteSpace(character) || character is '\'' or '"' or '$' or '`' or '&' or '|' or ';' or '<' or '>' or '(' or ')' or '*' or '?')) return new(null, word);
        var extensions = (windows ?? OperatingSystem.IsWindows()) ? new[] { "", ".exe", ".cmd", ".bat" } : new[] { "" };
        try
        {
            if (word.Contains('/') || word.Contains('\\'))
            {
                if (word.StartsWith('~')) return new(null, word);
                return new(extensions.Any(extension => File.Exists(word + extension)), word);
            }
            var search = path ?? Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(search)) return new(null, word);
            foreach (var directory in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Path.IsPathRooted(directory)) continue;
                if (extensions.Any(extension => File.Exists(Path.Combine(directory, word + extension)))) return new(true, word);
            }
            return new(false, word);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return new(null, word); }
    }
}
