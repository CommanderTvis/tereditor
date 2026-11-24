using System.Text;
using System.Text.RegularExpressions;

using Tereditor.Checks.Design;

namespace Tereditor.Checks;

/// <summary>
/// Text a person reads (Markdown, .axaml and C# string literals) uses American English. Wire values, identifiers, file names and
/// third-party text are exempt: only whole words in prose are checked, and <c>Allowed</c> names the few literals that must stay.
/// </summary>
internal static partial class SpellingChecks
{
    [GeneratedRegex(@"(?<![\w.\-/:])(?:colour\w*|behaviour\w*|favourite\w*|centres?|centred|centring|licenc(?:e|es|ed)|organis(?:e|ed|es|ing|ation|ations)|catalogues?|catalogued|neighbour\w*|honour\w*|cancell(?:ed|ing)|travell(?:ed|ing|er|ers)|labell(?:ed|ing)|modell(?:ed|ing)|(?:synchronis|serialis|customis|recognis|initialis|normalis|visualis|optimis|summaris|minimis|maximis)(?:e|es|ed|ing|ation|ations)|greys?|cheques?|programmes?|analys(?:e|ed|ing)|artefacts?|whilst|learnt)(?![\w])", RegexOptions.IgnoreCase)]
    private static partial Regex British();

    [GeneratedRegex(@"colour", RegexOptions.IgnoreCase)]
    private static partial Regex Colour();

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex Literal();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Hole();

    private static readonly string[] SkippedDirectories = [".git", "bin", "obj", "node_modules", "licenses", "artifacts", ".bench", ".vs", "Fixtures"];

    /// <summary>Literals that carry a protocol or file name, so their spelling is not ours: MCP and ACP stop reasons, a GraphQL enum, test fixture names.</summary>
    private static readonly string[] Allowed =
    [
        "notifications/cancelled", "outcome cancelled", "cancelled:false", "Side question cancelled", "cancelled.txt", "-cancelled", "neighbour.md", "CANCELLED", "Labelled ✳", "\\\"cancelled\\\""
    ];

    internal static void Run(string? root = null)
    {
        root ??= DesignSources.Root();
        if (root is null) return;
        var failures = new List<string>();
        foreach (var path in Files(root))
        {
            var extension = Path.GetExtension(path);
            var relative = Path.GetRelativePath(root, path);
            var fenced = false;
            var comment = false;
            var number = 0;
            foreach (var raw in File.ReadLines(path))
            {
                number++;
                var line = raw;
                foreach (var allowed in Allowed) line = line.Replace(allowed, "", StringComparison.Ordinal);
                string prose;
                if (extension == ".md")
                {
                    if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
                    if (fenced) continue;
                    prose = Regex.Replace(Regex.Replace(line, "`[^`]*`", ""), @"\]\([^)]*\)", "]");
                }
                else if (extension == ".axaml")
                {
                    prose = Regex.Replace(line, "<!--.*?-->", "");
                    if (comment) { comment = !prose.Contains("-->", StringComparison.Ordinal); continue; }
                    if (prose.Contains("<!--", StringComparison.Ordinal)) { comment = true; prose = prose[..prose.IndexOf("<!--", StringComparison.Ordinal)]; }
                }
                else
                {
                    if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    var sentences = new StringBuilder();
                    foreach (Match literal in Literal().Matches(line))
                    {
                        var body = Hole().Replace(literal.Value[1..^1], "");
                        if (!Colour().IsMatch(body) && body.All(c => char.IsLower(c) || c is '_' or '-')) continue;
                        sentences.Append(body).Append(' ');
                    }
                    prose = sentences.ToString();
                }
                foreach (Match match in British().Matches(prose)) failures.Add($"{relative}:{number}: \"{match.Value}\" is British spelling; use the American form");
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures.Take(40)) + (failures.Count > 40 ? $"{Environment.NewLine}and {failures.Count - 40} more" : ""));
        Console.WriteLine("PASS spelling: user-visible text uses American English");
    }

    private static IEnumerable<string> Files(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (name is "SpellingChecks.cs" or "CLAUDE.local.md" or "NOTICE" or "THIRD-PARTY-NOTICES.md" || name.EndsWith(".g.cs", StringComparison.Ordinal)) continue;
            if (Path.GetExtension(file) is ".md" or ".axaml" or ".cs") yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
            if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                foreach (var file in Files(child)) yield return file;
    }
}