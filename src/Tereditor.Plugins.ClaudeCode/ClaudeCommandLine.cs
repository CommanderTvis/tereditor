using System.Text;
using System.Text.RegularExpressions;

namespace Tereditor.Plugins.ClaudeCode;

/// <summary>One leading <c>NAME=value</c> of the launch command: the parsed value and the text as the user wrote it.</summary>
public sealed record ClaudeAssignment(string Name, string Value, string Source);

/// <summary>
/// The Claude Code launch command setting read as a shell would read its start: leading <c>NAME=value</c> assignments,
/// the executable, then its own arguments, with single and double quotes honoured. Nothing is run by a shell. Every
/// background run of the binary (auth, usage, chat, context reads) and the terminal launch line take their parts from here.
/// Names starting with <c>TEREDITOR_</c> or <c>THINKRAIL_</c> are dropped: they belong to tereditor. Variables that tereditor
/// or another plugin's launch contribution set for the same run win over an assignment of the setting.
/// </summary>
public sealed partial record ClaudeCommandLine(IReadOnlyList<ClaudeAssignment> Assignments, string Executable, IReadOnlyList<string> Arguments, string Rest)
{
    public const string DefaultExecutable = "claude";

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)=")]
    private static partial Regex Assignment();

    /// <summary>The assignments as a variable map, for a process start.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; } = Assignments.ToDictionary(assignment => assignment.Name, assignment => assignment.Value, StringComparer.Ordinal);

    public static ClaudeCommandLine Parse(string? command)
    {
        var text = (command ?? "").Trim();
        var assignments = new List<ClaudeAssignment>();
        var words = new List<string>();
        var restStart = text.Length;
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index >= text.Length) break;
            var start = index;
            var word = new StringBuilder();
            char? quote = null;
            for (; index < text.Length; index++)
            {
                var character = text[index];
                if (quote is { } open) { if (character == open) quote = null; else word.Append(character); }
                else if (character is '\'' or '"') quote = character;
                else if (char.IsWhiteSpace(character)) break;
                else word.Append(character);
            }
            var value = word.ToString();
            if (words.Count == 0 && restStart == text.Length && Assignment().Match(text.AsSpan(start, index - start).ToString()) is { Success: true } match)
            {
                if (!Reserved(match.Groups[1].Value))
                    assignments.Add(new(match.Groups[1].Value, value[(match.Length)..], text[start..index]));
                continue;
            }
            if (restStart == text.Length) restStart = start;
            words.Add(value);
        }
        var rest = text[restStart..];
        return new(assignments, words.Count > 0 ? words[0] : DefaultExecutable, [.. words.Skip(1)], rest);
    }

    private static bool Reserved(string name) =>
        name.StartsWith("TEREDITOR_", StringComparison.Ordinal) || name.StartsWith("THINKRAIL_", StringComparison.Ordinal);

    /// <summary>
    /// The assignments as a prefix of a command line for the shell that will read it, with a trailing space, or empty
    /// when there are none. POSIX shells read them as written; <c>cmd</c> and PowerShell get their own set syntax.
    /// </summary>
    public string ShellPrefix(bool windows, string? windowsShell)
    {
        if (Assignments.Count == 0) return "";
        if (!windows) return string.Concat(Assignments.Select(assignment => assignment.Source + " "));
        return windowsShell == "cmd"
            ? string.Concat(Assignments.Select(assignment => $"set \"{assignment.Name}={assignment.Value}\" && "))
            : string.Concat(Assignments.Select(assignment => $"$env:{assignment.Name}='{assignment.Value.Replace("'", "''", StringComparison.Ordinal)}'; "));
    }
}