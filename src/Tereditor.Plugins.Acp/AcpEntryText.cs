namespace Tereditor.Plugins.Acp;

/// <summary>How the settings page writes and reads an agent's arguments and environment as plain text.</summary>
public static class AcpEntryText
{
    private static readonly IReadOnlyDictionary<string, string> SupportLines = new Dictionary<string, string>
    {
        ["both"] = "Gets tereditor's tools and instructions.",
        ["tools"] = "Gets tereditor's tools. It takes no instructions from tereditor.",
        ["instructions"] = "Gets tereditor's instructions. It cannot connect to tereditor's tools.",
        ["none"] = "Gets neither tereditor's tools nor its instructions. This agent has no way to take them."
    };

    /// <summary>The settings line on what tereditor gives an agent, from <see cref="AcpAgentEntry.TereditorSupport"/>.</summary>
    public static string SupportLine(string support) =>
        SupportLines.GetValueOrDefault(support) ?? "Tereditor's tools and instructions: known after its first chat.";

    /// <summary>One <c>NAME=value</c> per line; blank lines and lines without a name are dropped.</summary>
    public static IReadOnlyDictionary<string, string> ParseEnvironment(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var at = line.IndexOf('=');
            if (at > 0) result[line[..at].Trim()] = line[(at + 1)..];
        }
        return result;
    }

    public static string FormatEnvironment(IReadOnlyDictionary<string, string> environment) =>
        string.Join('\n', environment.Select(pair => pair.Key + "=" + pair.Value));

    /// <summary>Splits a command line into arguments; double or single quotes keep a space inside one argument.</summary>
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        var started = false;
        foreach (var c in text)
        {
            if (quote is { } open)
            {
                if (c == open) quote = null;
                else current.Append(c);
            }
            else if (c is '"' or '\'') { quote = c; started = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (started || current.Length > 0) arguments.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else current.Append(c);
        }
        if (started || current.Length > 0) arguments.Add(current.ToString());
        return arguments;
    }

    /// <summary>
    /// How an entry starts, the one place the launcher and the settings page both ask: a registry npx agent with no executable override runs with
    /// <paramref name="bun"/> when there is one, otherwise (and for any command the user chose) its own command runs as it is.
    /// </summary>
    public static (string Command, IReadOnlyList<string> Args, bool UsesBun) Resolve(AcpAgentEntry entry, string? bun)
    {
        var command = entry.LaunchCommand();
        IReadOnlyList<string> args = entry.LaunchArgs();
        if (entry.CommandOverride.Length == 0 && entry.Origin == "registry" && command == "npx" && !string.IsNullOrEmpty(bun))
        {
            var (bunCommand, bunArgs) = Bun(bun, args);
            return (bunCommand, bunArgs, true);
        }
        return (command, args, false);
    }

    /// <summary>The npx arguments as Bun runs them: <c>bun x</c> asks nothing, so a leading <c>-y</c> or <c>--yes</c> is dropped.</summary>
    public static (string Command, IReadOnlyList<string> Args) Bun(string bun, IReadOnlyList<string> npxArgs) =>
        (bun, ["x", .. npxArgs.SkipWhile(argument => argument is "-y" or "--yes")]);

    /// <summary>The line the user sees for an entry: what the launcher runs (<see cref="Resolve"/>; Bun shows as bunx, which is how it is started), arguments with spaces quoted.</summary>
    public static string CommandLine(AcpAgentEntry entry)
    {
        var (command, args, usesBun) = Resolve(entry, "bun");
        return string.Join(' ', (usesBun ? new[] { "bunx" }.Concat(args.Skip(1)) : new[] { command }.Concat(args)).Select(part => part.Contains(' ') ? "\"" + part + "\"" : part));
    }

    /// <summary>One argument per line; blank lines are dropped and a trailing carriage return is not part of the argument.</summary>
    public static IReadOnlyList<string> ParseLines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0)];

    /// <summary>A stable id for a custom agent from its name.</summary>
    public static string IdFor(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        return "custom-" + (slug.Length > 0 ? slug : Guid.NewGuid().ToString("N")[..8]);
    }
}