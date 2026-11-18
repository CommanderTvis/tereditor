namespace Tereditor.Plugins.ClaudeCode;

/// <summary>
/// The tools every Claude Code session tereditor starts has switched off, as one command-line flag, without touching the
/// user's settings files. Terminal lines, chat children and context measurements all take it from here.
/// </summary>
public static class ClaudeToolPolicy
{
    /// <summary>tereditor has its own workspace tools, so the CLI's built-in worktree entry stays off.</summary>
    public const string Disallowed = "EnterWorktree";

    public const string Flag = "--disallowedTools=" + Disallowed;

    private static readonly string[] DenyFlags = ["--disallowedTools", "--disallowed-tools"];

    /// <summary>
    /// The denial to add to a line whose own words are <paramref name="words"/>, or null when the line already denies the
    /// tool or leaves it out with an explicit <c>--tools</c> list. The flag may repeat on a line, so adding it merges with
    /// the user's own denials.
    /// </summary>
    public static string? FlagFor(IEnumerable<string> words)
    {
        var list = words.SelectMany(word => word.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Select(word => word.Trim('"', '\'')).ToList();
        for (var index = 0; index < list.Count; index++)
        {
            var equals = list[index].IndexOf('=', StringComparison.Ordinal);
            var name = equals > 0 ? list[index][..equals] : list[index];
            var deny = DenyFlags.Contains(name);
            if (!deny && name != "--tools") continue;
            var values = new List<string>();
            if (equals > 0) values.Add(list[index][(equals + 1)..]);
            else
                for (var next = index + 1; next < list.Count && !list[next].StartsWith('-'); next++) values.Add(list[next]);
            var tools = values.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
            if (tools.Contains(Disallowed, StringComparer.Ordinal)) return null;
            if (!deny && tools.Count > 0 && !tools.Contains("default", StringComparer.Ordinal)) return null;
        }
        return Flag;
    }
}