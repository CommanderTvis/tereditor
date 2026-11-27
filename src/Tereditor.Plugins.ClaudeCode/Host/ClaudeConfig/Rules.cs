using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Tereditor.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>
/// Reads <c>.claude/rules</c> as Claude Code does (https://code.claude.com/docs/en/memory, and Claude Code 2.1.332 where
/// the page is silent): every <c>.md</c> file under the user's folder, then under each folder from the top down to the
/// session's own, with links followed. The rules of folders below the session's are a separate search of the project
/// (<see cref="Below"/>), since it costs more. The walks and each read are bounded.
/// </summary>
internal static partial class ClaudeRules
{
    private const int MaxFiles = 500;
    private const int MaxEntries = 5000;
    private const int MaxFolderDepth = 16;
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxHead = 256 * 1024;
    private const int MaxYamlDepth = 32;
    private const int MaxPatterns = 200;
    private const int MaxSearchFolders = 20_000;
    private const int MaxSearchDepth = 32;
    private static readonly TimeSpan MaxSearchTime = TimeSpan.FromSeconds(5);

    // Version control, dependencies and build output. A .claude folder is not searched either: its worktrees hold copies of the project.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".claude", "node_modules", "bin", "obj", "target", "dist", "build", "vendor", ".venv", "venv", "__pycache__",
        ".gradle", ".dart_tool", ".next", ".cache"
    };

    private const string InvalidYaml = "Paths are ignored: the settings at the top are not valid YAML.";
    private const string AsksFirst = "Loads only if you allow external imports. The link leads outside the session's folder.";
    private const string NeverLoads = "Claude Code does not load this rule. It has paths, and the link leads outside the session's folder.";
    private const string NeverLoadsBelow = "Claude Code does not load this rule. The link leads outside the session's folder.";
    private const string TooLarge = "Claude Code skips files over 4 MB.";
    private const string LongSettings = "The settings at the top are too long to check here.";
    private const string Unreadable = "This file cannot be read.";

    private sealed class Walk(string cwd)
    {
        public string Cwd { get; } = cwd;
        public List<ClaudeContextLayer> Layers { get; } = [];
        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public int Entries { get; set; }
        public bool Truncated { get; set; }
    }

    /// <summary>The rule files a session in <paramref name="cwd"/> can load, in Claude Code's order; <c>Truncated</c> when a limit cut the list.</summary>
    public static (IReadOnlyList<ClaudeContextLayer> Layers, bool Truncated) Collect(string cwd)
    {
        var walk = AtStart(cwd);
        return (walk.Layers, walk.Truncated);
    }

    private static Walk AtStart(string cwd)
    {
        var walk = new Walk(ClaudePaths.RealPath(cwd) ?? cwd);
        foreach (var rules in ClaudePaths.RulesDirectories(cwd)) Add(walk, rules);
        return walk;
    }

    /// <summary>Whether <see cref="Below"/> searches this folder: not the home folder or one above it, which would walk all of the user's files.</summary>
    public static bool SearchesBelow(string cwd)
    {
        var folder = ClaudePaths.RealPath(cwd) ?? cwd;
        var home = ClaudePaths.RealPath(ClaudeEnvironment.Home()) ?? ClaudeEnvironment.Home();
        return Path.GetDirectoryName(folder) is not null && Path.TrimEndingDirectorySeparator(home) != Path.TrimEndingDirectorySeparator(folder) && !IsInside(home, folder);
    }

    /// <summary>
    /// The rules in <c>.claude/rules</c> folders below <paramref name="cwd"/>, shallowest first; Claude Code loads them on demand.
    /// Links to folders are not searched, nor are <see cref="Skipped"/> names and the <paramref name="ignored"/> full paths.
    /// </summary>
    public static ClaudeNestedRules Below(string cwd, IReadOnlySet<string> ignored, CancellationToken cancellationToken)
    {
        if (!SearchesBelow(cwd))
            return new(cwd, [], [new(ClaudeProblemSeverity.Info, "Rules in subfolders are not listed",
                "This session starts in your home folder or above it, so its subfolders are not searched.")]);
        // What the session loads at start is read first, so a link back to one of those files does not list it twice.
        var walk = AtStart(cwd);
        walk.Layers.Clear();
        walk.Entries = 0;
        walk.Truncated = false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        var clock = Stopwatch.StartNew();
        var pending = new Queue<(string Folder, int Depth)>([(root, 0)]);
        var queued = 1;
        var partial = false;
        while (pending.TryDequeue(out var next))
        {
            var (folder, depth) = next;
            var children = new List<string>();
            var configured = false;
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.Elapsed > MaxSearchTime) { partial = true; break; }
                    if (entry.Name == ".claude") configured = depth > 0;
                    else if (entry is DirectoryInfo && (entry.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        !Skipped.Contains(entry.Name) && !ignored.Contains(entry.FullName)) children.Add(entry.FullName);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (configured)
                Add(walk, new(ClaudeConfigScope.Project, Path.Combine(folder, ".claude", "rules"), folder)
                { Below = Path.GetRelativePath(root, folder).Replace(Path.DirectorySeparatorChar, '/') });
            if (clock.Elapsed > MaxSearchTime) { partial = true; break; }
            foreach (var child in children.Order(StringComparer.Ordinal))
            {
                if (depth >= MaxSearchDepth || queued >= MaxSearchFolders) { partial = true; break; }
                queued++;
                pending.Enqueue((child, depth + 1));
            }
        }
        var cut = partial ? "This project is too large to search fully." : walk.Truncated ? "The rule folders hold too much to list." : null;
        return new(cwd, walk.Layers, cut is null ? [] : [new(ClaudeProblemSeverity.Warning, "Some rules in subfolders are not listed", cut)]);
    }

    private static void Add(Walk walk, RulesDirectory rules)
    {
        if (ClaudePaths.RealPath(rules.Path) is not { } real || !Directory.Exists(real)) return;
        // A .claude or rules folder that is itself a link counts as linked, like any link below it.
        var linked = ClaudePaths.RealPath(rules.Base) is { } owner && real != Path.Combine(owner, ".claude", "rules");
        Visit(walk, rules, rules.Path, real, "", linked, new FileInfo(rules.Path).LinkTarget is not null, 0);
    }

    // linked: some link leads to this folder; within: that link is the rules folder itself or one inside it.
    private static void Visit(Walk walk, RulesDirectory rules, string folder, string real, string prefix, bool linked, bool within, int depth)
    {
        if (!walk.Folders.Add(real)) return;
        if (depth > MaxFolderDepth) { walk.Truncated = true; return; }
        List<(string Name, bool Link)> entries = [];
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(real))
            {
                if (walk.Entries >= MaxEntries) { walk.Truncated = true; break; }
                walk.Entries++;
                entries.Add((Path.GetFileName(path), new FileInfo(path).LinkTarget is not null));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        // Real entries first, so a file reached both directly and through a link keeps its own name.
        foreach (var (name, link) in entries.OrderBy(entry => entry.Link).ThenBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var target = link ? ClaudePaths.RealPath(Path.Combine(real, name)) : Path.Combine(real, name);
            if (target is null) continue;
            if (Directory.Exists(target)) Visit(walk, rules, Path.Combine(folder, name), target, prefix + name + "/", linked || link, within || link, depth + 1);
            else if (name.EndsWith(".md", StringComparison.Ordinal) && File.Exists(target))
            {
                if (walk.Layers.Count >= MaxFiles) { walk.Truncated = true; return; }
                if (!walk.Files.Add(target)) continue;
                var leaves = rules.Scope == ClaudeConfigScope.Project && !IsInside(target, walk.Cwd);
                // On demand Claude Code never follows a link out of the session's folder, approved or not.
                var barred = rules.Below is not null && (within || link) && leaves;
                walk.Layers.Add(Layer(rules, Path.Combine(folder, name), prefix + name, target, leaves && (linked || link), barred));
            }
        }
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // Lazy is false for a rule every session loads, true for one that waits for Claude to reach a file, and null when neither can be promised.
    private static ClaudeContextLayer Layer(RulesDirectory rules, string path, string label, string target, bool outside, bool barred)
    {
        var layer = new ClaudeContextLayer(ClaudeContextKind.Rules, label, path, new(rules.Scope, path), 0) { Folder = rules.Below };
        string head;
        bool cut;
        try
        {
            layer = layer with { Bytes = new FileInfo(target).Length };
            if (barred) return layer with { Note = NeverLoadsBelow };
            if (layer.Bytes > MaxFileBytes) return layer with { Note = TooLarge };
            // An empty file has no settings; not opening it also keeps a pipe or device named *.md from blocking the read.
            (head, cut) = layer.Bytes == 0 ? ("", false) : ReadHead(target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return layer with { Note = Unreadable }; }
        var (globs, invalid, unclosed) = Paths(head);
        if (unclosed && cut) return layer with { Note = LongSettings };
        if (globs is not null)
            return layer with { PathGlobs = globs, PatternBase = rules.Base, Lazy = true, Note = outside ? NeverLoads : null };
        return outside ? layer with { Note = AsksFirst } : layer with { Lazy = rules.Below is not null, Note = invalid ? InvalidYaml : null };
    }

    private static (string Head, bool Cut) ReadHead(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[MaxHead];
        var length = reader.ReadBlock(buffer);
        return (new string(buffer, 0, length), reader.Peek() >= 0);
    }

    /// <summary>
    /// The <c>paths</c> patterns that scope a rule, or null when it loads in every session. <c>Invalid</c>: the settings do
    /// not parse. <c>Unclosed</c>: they open and never close in this text.
    /// </summary>
    private static (IReadOnlyList<string>? Globs, bool Invalid, bool Unclosed) Paths(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal)) return default;
        var start = -1;
        for (var index = 3; index < text.Length && char.IsWhiteSpace(text[index]); index++)
            if (text[index] == '\n') start = index + 1;
        if (start < 0) return default;
        var end = text.IndexOf("---", start, StringComparison.Ordinal);
        if (end < 0) return (null, false, true);
        var yaml = text[start..end];
        // Claude Code reads a value such as **/*.ts, which is not valid YAML, by quoting it and parsing again.
        if ((Parse(yaml) ?? Parse(Quoted(yaml))) is not { } values) return (null, true, false);
        var patterns = values.SelectMany(Split).Where(pattern => WithoutTrailingStars(pattern).Length > 0).ToList();
        // A pattern that matches everything scopes nothing.
        return patterns.All(pattern => WithoutTrailingStars(pattern) == "**") ? default : (patterns, false, false);
    }

    private static string WithoutTrailingStars(string pattern) => pattern.EndsWith("/**", StringComparison.Ordinal) ? pattern[..^3] : pattern;

    // A comma separates patterns, except inside braces: src/*.{ts,tsx} is one.
    private static IEnumerable<string> Split(string value)
    {
        var depth = 0;
        var start = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length)
            {
                if (value[index] == '{') depth++;
                else if (value[index] == '}') depth--;
                if (value[index] != ',' || depth != 0) continue;
            }
            if (value[start..index].Trim() is { Length: > 0 } pattern) yield return pattern;
            start = index + 1;
        }
    }

    [GeneratedRegex(@"^([a-zA-Z_-]+):\s+(\S[^\r]*)$")]
    private static partial Regex TopLevelValue();

    [GeneratedRegex(@"[{}\[\]*&#!|>%@`]|: ")]
    private static partial Regex NeedsQuotes();

    [GeneratedRegex(@"^\t+", RegexOptions.Multiline)]
    private static partial Regex LeadingTabs();

    private static string Quoted(string yaml)
    {
        var lines = yaml.Split('\n').Select(line =>
        {
            if (TopLevelValue().Match(line) is not { Success: true } match) return line;
            var value = match.Groups[2].Value;
            if (value.StartsWith('"') && value.EndsWith('"') || value.StartsWith('\'') && value.EndsWith('\'')) return line;
            if (value.StartsWith('[') && value.EndsWith(']') && Parse("paths: " + value) is not null) return line;
            return NeedsQuotes().IsMatch(value)
                ? $"{match.Groups[1].Value}: \"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
                : line;
        });
        return LeadingTabs().Replace(string.Join('\n', lines), tabs => new string(' ', tabs.Length * 2));
    }

    private sealed class Node(bool mapping, bool collects)
    {
        public bool Mapping { get; } = mapping;
        public bool Collects { get; } = collects;
        public bool AtKey { get; set; } = mapping;
        public bool PathsNext { get; set; }
    }

    // The strings under the top-level paths key, or null when the text is not YAML. Read as events, never as a tree: nesting
    // is capped and an alias is never expanded, so no file can exhaust the stack or the memory.
    private static List<string>? Parse(string yaml)
    {
        var values = new List<string>();
        var open = new Stack<Node>();
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var parser = new Parser(new StringReader(yaml));
            while (parser.MoveNext())
            {
                var current = parser.Current!;
                if (current is MappingEnd or SequenceEnd) { open.Pop(); continue; }
                if (current is not (Scalar or MappingStart or SequenceStart or AnchorAlias)) continue;
                var isKey = false;
                var collects = false;
                var top = false;
                if (open.TryPeek(out var parent))
                {
                    top = open.Count == 1 && parent.Mapping;
                    isKey = parent.Mapping && parent.AtKey;
                    collects = !isKey && (parent.Mapping ? parent.PathsNext : parent.Collects);
                    if (parent.Mapping) parent.AtKey = !parent.AtKey;
                    if (top && isKey)
                    {
                        parent.PathsNext = current is Scalar { Value: "paths" };
                        if (parent.PathsNext) values.Clear();
                    }
                }
                if (current is AnchorAlias alias)
                {
                    if (!anchors.Contains(alias.Value.Value)) return null;
                    continue;
                }
                if (current is NodeEvent { Anchor.IsEmpty: false } anchored) anchors.Add(anchored.Anchor.Value);
                if (current is Scalar scalar)
                {
                    if (collects && IsText(scalar) && values.Count < MaxPatterns) values.Add(scalar.Value);
                    continue;
                }
                if (open.Count >= MaxYamlDepth) return null;
                var mapping = current is MappingStart;
                open.Push(new(mapping, collects && !mapping));
            }
        }
        catch (Exception error) when (error is YamlException or ArgumentException or InvalidOperationException) { return null; }
        return values;
    }

    // A bare null, boolean or number is not a pattern.
    private static bool IsText(Scalar scalar) => scalar.Style != ScalarStyle.Plain ||
        scalar.Value is not ("" or "~" or "null" or "Null" or "NULL" or "true" or "True" or "TRUE" or "false" or "False" or "FALSE") &&
        !double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}