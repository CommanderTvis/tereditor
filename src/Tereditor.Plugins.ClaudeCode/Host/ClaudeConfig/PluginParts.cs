using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tereditor.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>
/// What an installed plugin adds: its skills, commands, agents, hooks, MCP and LSP servers, output styles, workflows, themes
/// and monitors, read from the files in its install folder (<c>installed_plugins.json</c> names the folder). Files are only
/// read, never run. Every path must stay inside the plugin folder, a file over the cap is not parsed, and the descriptions
/// carry no secret: a server shows its transport or file types, a hook its type, never a command, argument, env value or header.
/// </summary>
internal static class PluginParts
{
    private const long JsonCap = 512 * 1024;
    private const long MarketplaceCap = 8 * 1024 * 1024;
    private const long MarkdownCap = 1024 * 1024;
    private const int HeadBytes = 8 * 1024;
    private const int MaxParts = 500;
    private const int DescriptionLength = 200;

    /// <summary>The parts of <paramref name="pluginId"/> (<c>name@marketplace</c>), or null when its files are not found.</summary>
    public static IReadOnlyList<ClaudePluginPart>? Read(string pluginId, string projectRoot)
    {
        try
        {
            return ReadCore(pluginId, projectRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ClaudePluginPart>? ReadCore(string pluginId, string projectRoot)
    {
        var at = pluginId.IndexOf('@');
        var name = at > 0 ? pluginId[..at] : pluginId;
        if (InstallPath(pluginId, projectRoot) is not { } root || !Directory.Exists(root)) return null;
        var manifestPath = Path.Combine(root, ".claude-plugin", "plugin.json");
        var manifest = ReadJson(manifestPath, JsonCap) ?? [];
        var entry = at > 0 ? MarketplaceEntry(pluginId[(at + 1)..], name) : null;
        JsonNode? Field(string key) => manifest[key] ?? entry?[key];
        var context = new Context(Path.GetFullPath(root), name, manifestPath, new(StringComparer.Ordinal));
        var parts = new List<ClaudePluginPart>();
        Skills(context, Field("skills"), Field("skills") is null, parts);
        Commands(context, Field("commands"), parts);
        Agents(context, Field("agents"), parts);
        Hooks(context, manifest["hooks"] ?? entry?["hooks"], parts);
        Servers(context, manifest["mcpServers"] ?? entry?["mcpServers"], ".mcp.json", "mcpServers", ClaudePluginPartKind.Mcp, parts);
        Servers(context, manifest["lspServers"] ?? entry?["lspServers"], ".lsp.json", null, ClaudePluginPartKind.Lsp, parts);
        Styles(context, Field("outputStyles"), parts);
        Files(context, Field("workflows"), "workflows", ".js", ClaudePluginPartKind.Workflow, parts);
        var experimental = manifest["experimental"] as JsonObject;
        Files(context, experimental?["themes"] ?? Field("themes"), "themes", ".json", ClaudePluginPartKind.Theme, parts);
        Monitors(context, experimental?["monitors"] ?? Field("monitors"), parts);
        return parts.Count > MaxParts ? parts.GetRange(0, MaxParts) : parts;
    }

    private sealed record Context(string Root, string Plugin, string ManifestPath, HashSet<string> Seen)
    {
        public string Qualified(string name) => $"{Plugin}:{name}";

        /// <summary>The file that holds an inline declaration, or null when the plugin has no manifest of its own (the marketplace declared it).</summary>
        public string? InlinePath => File.Exists(ManifestPath) ? ManifestPath : null;
    }

    private static IEnumerable<JsonNode?> Items(JsonNode? declared) => declared is JsonArray array ? array.ToArray() : new JsonNode?[] { declared };

    // The roots to scan: what the manifest names, or the default folder when it names nothing.
    private static IEnumerable<string> Roots(Context context, JsonNode? declared, string defaultFolder) =>
        (declared is null ? [Inside(context, defaultFolder)] : PathList(declared).Select(relative => Inside(context, relative))).OfType<string>();

    // The folder the plugin was installed to: the entry for this project when there is one, else the user's, else the first.
    private static string? InstallPath(string pluginId, string projectRoot)
    {
        var installed = ReadJson(Path.Combine(ClaudePaths.ClaudeHome(), "plugins", "installed_plugins.json"), JsonCap * 4);
        if (installed?["plugins"] is not JsonObject plugins || plugins[pluginId] is not JsonArray entries) return null;
        var records = entries.OfType<JsonObject>().Where(record => Json.String(record["installPath"]) is { Length: > 0 }).ToArray();
        var chosen = records.FirstOrDefault(record => Json.String(record["projectPath"]) == projectRoot)
            ?? records.FirstOrDefault(record => Json.String(record["scope"]) == "user") ?? records.FirstOrDefault();
        return chosen is null ? null : Json.String(chosen["installPath"]);
    }

    // A marketplace entry may declare the plugin's parts inline (strict: false), with no files in the plugin folder.
    private static JsonObject? MarketplaceEntry(string marketplace, string plugin)
    {
        if (marketplace.Length == 0 || marketplace.IndexOfAny(['/', '\\']) >= 0 || marketplace.Contains("..", StringComparison.Ordinal)) return null;
        var path = Path.Combine(ClaudePaths.ClaudeHome(), "plugins", "marketplaces", marketplace, ".claude-plugin", "marketplace.json");
        return ReadJson(path, MarketplaceCap)?["plugins"] is JsonArray list
            ? list.OfType<JsonObject>().FirstOrDefault(item => Json.String(item["name"]) == plugin) : null;
    }

    private static JsonObject? ReadJson(string path, long cap)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > cap || info.LinkTarget is not null) return null;
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static IEnumerable<string> PathList(JsonNode? value) => value switch
    {
        JsonValue => Json.String(value) is { } one ? [one] : [],
        JsonArray array => array.Select(Json.String).OfType<string>(),
        _ => []
    };

    // A path from a manifest, inside the plugin folder and not a link; anything else is ignored.
    private static string? Inside(Context context, string relative)
    {
        var trimmed = relative.Trim();
        if (trimmed.Length == 0 || Path.IsPathRooted(trimmed) || trimmed.Split('/', '\\').Contains("..")) return null;
        var full = Path.GetFullPath(Path.Combine(context.Root, trimmed));
        var rooted = Path.TrimEndingDirectorySeparator(context.Root);
        if (full != rooted && !full.StartsWith(rooted + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return IsLink(full) ? null : full;
    }

    private static bool IsLink(string path) =>
        (File.Exists(path) ? new FileInfo(path) as FileSystemInfo : Directory.Exists(path) ? new DirectoryInfo(path) : null)?.LinkTarget is not null;

    private static IEnumerable<string> Children(string directory, bool directories)
    {
        if (!Directory.Exists(directory)) return [];
        var entries = directories ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFiles(directory);
        return entries.Where(path => !IsLink(path)).OrderBy(path => path, StringComparer.Ordinal).Take(MaxParts);
    }

    private static void Add(Context context, List<ClaudePluginPart> parts, ClaudePluginPartKind kind, string name, string? description, string? path)
    {
        if (parts.Count >= MaxParts || !context.Seen.Add($"{kind}\0{name}\0{path}")) return;
        parts.Add(new(kind, name) { Description = description, Path = path });
    }

    /// <summary>
    /// The name and the one-line description in a Markdown file's frontmatter. Only the head of a file is read, and a file
    /// over the cap is not read at all.
    /// </summary>
    internal static (string? Name, string? Description, bool TooLarge) Frontmatter(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return (null, null, false);
        if (info.Length > MarkdownCap) return (null, null, true);
        var buffer = new byte[Math.Min(HeadBytes, (int)info.Length)];
        using (var stream = File.OpenRead(path)) stream.ReadExactly(buffer);
        var lines = Encoding.UTF8.GetString(buffer).Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, null, false);
        string? name = null, description = null;
        for (var index = 1; index < lines.Length && lines[index].Trim() != "---"; index++)
        {
            var colon = lines[index].IndexOf(':');
            if (colon <= 0 || lines[index][0] is ' ' or '\t') continue;
            var key = lines[index][..colon].Trim();
            if (key is not ("name" or "description")) continue;
            var value = lines[index][(colon + 1)..].Trim();
            if (value is ">" or "|" or ">-" or "|-" or ">+" or "|+" or "")
            {
                var folded = new List<string>();
                while (index + 1 < lines.Length && (lines[index + 1].StartsWith(' ') || lines[index + 1].StartsWith('\t')))
                    folded.Add(lines[++index].Trim());
                value = string.Join(' ', folded);
            }
            value = Unquote(value);
            if (key == "name") name = value; else description = OneLine(value);
        }
        return (name, description, false);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;

    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > DescriptionLength ? line[..(DescriptionLength - 1)] + "…" : line;
    }

    private const string TooLarge = "File too large to read";

    private static void AddMarkdown(Context context, List<ClaudePluginPart> parts, ClaudePluginPartKind kind, string fallbackName, string file, bool nameFromFile)
    {
        var (name, description, tooLarge) = Frontmatter(file);
        Add(context, parts, kind, context.Qualified(nameFromFile && !string.IsNullOrWhiteSpace(name) ? name.Trim() : fallbackName), tooLarge ? TooLarge : description, file);
    }

    private static void Skills(Context context, JsonNode? declared, bool noKey, List<ClaudePluginPart> parts)
    {
        var folders = new List<string>();
        var defaults = Inside(context, "skills");
        if (defaults is not null) folders.Add(defaults);
        foreach (var relative in PathList(declared))
            if (Inside(context, relative) is { } folder) folders.Add(folder);
        var found = false;
        foreach (var folder in folders.Distinct())
        {
            if (File.Exists(Path.Combine(folder, "SKILL.md"))) { Skill(context, parts, folder); found = true; continue; }
            foreach (var child in Children(folder, true))
                if (File.Exists(Path.Combine(child, "SKILL.md"))) { Skill(context, parts, child); found = true; }
        }
        // A plugin with SKILL.md at its root and no skills folder or key is one skill.
        if (!found && noKey && !Directory.Exists(Path.Combine(context.Root, "skills")) && File.Exists(Path.Combine(context.Root, "SKILL.md")))
            Skill(context, parts, context.Root);
    }

    private static void Skill(Context context, List<ClaudePluginPart> parts, string folder) =>
        AddMarkdown(context, parts, ClaudePluginPartKind.Skill, folder == context.Root ? context.Plugin : Path.GetFileName(folder), Path.Combine(folder, "SKILL.md"), true);

    private static IEnumerable<string> MarkdownIn(string path, bool recurse, int depth = 0)
    {
        if (File.Exists(path)) { if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) yield return path; yield break; }
        foreach (var file in Children(path, false))
            if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) yield return file;
        if (!recurse || depth >= 3) yield break;
        foreach (var child in Children(path, true))
            foreach (var file in MarkdownIn(child, true, depth + 1)) yield return file;
    }

    private static void Commands(Context context, JsonNode? declared, List<ClaudePluginPart> parts)
    {
        if (declared is JsonObject map)
        {
            foreach (var (key, value) in map)
            {
                var record = value as JsonObject;
                var source = Json.String(record?["source"]) is { } relative ? Inside(context, relative) : null;
                var description = OneLine(Json.String(record?["description"])) ?? (source is not null && File.Exists(source) ? Frontmatter(source).Description : null);
                Add(context, parts, ClaudePluginPartKind.Command, context.Qualified(key), description, source is not null && File.Exists(source) ? source : null);
            }
            return;
        }
        foreach (var root in Roots(context, declared, "commands"))
            foreach (var file in MarkdownIn(root, false))
                AddMarkdown(context, parts, ClaudePluginPartKind.Command, Path.GetFileNameWithoutExtension(file), file, false);
    }

    private static void Agents(Context context, JsonNode? declared, List<ClaudePluginPart> parts)
    {
        foreach (var root in Roots(context, declared, "agents"))
            foreach (var file in MarkdownIn(root, true))
            {
                var relative = Path.GetRelativePath(Directory.Exists(root) ? root : Path.GetDirectoryName(root)!, file);
                var stem = relative[..^Path.GetExtension(relative).Length].Replace(Path.DirectorySeparatorChar, ':');
                AddMarkdown(context, parts, ClaudePluginPartKind.Agent, stem, file, true);
            }
    }

    private static void Styles(Context context, JsonNode? declared, List<ClaudePluginPart> parts)
    {
        foreach (var root in Roots(context, declared, "output-styles"))
            foreach (var file in MarkdownIn(root, false))
                AddMarkdown(context, parts, ClaudePluginPartKind.OutputStyle, Path.GetFileNameWithoutExtension(file), file, true);
    }

    private static void Files(Context context, JsonNode? declared, string folder, string extension, ClaudePluginPartKind kind, List<ClaudePluginPart> parts)
    {
        foreach (var root in Roots(context, declared, folder))
        {
            IEnumerable<string> files = File.Exists(root) ? [root] : Children(root, false);
            foreach (var file in files.Where(path => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                Add(context, parts, kind, context.Qualified(Path.GetFileNameWithoutExtension(file)), null, file);
        }
    }

    private static void Monitors(Context context, JsonNode? declared, List<ClaudePluginPart> parts)
    {
        var sources = new List<(JsonNode? Node, string Path)>();
        if (declared is JsonArray inline) sources.Add((inline, context.InlinePath ?? ""));
        else if (Json.String(declared) is { } relative && Inside(context, relative) is { } file) sources.Add((ReadFileNode(file), file));
        else if (Inside(context, Path.Combine("monitors", "monitors.json")) is { } fallback) sources.Add((ReadFileNode(fallback), fallback));
        foreach (var (node, path) in sources)
            if (node is JsonArray list)
                foreach (var monitor in list.OfType<JsonObject>())
                    if (Json.String(monitor["name"]) is { Length: > 0 } monitorName)
                        Add(context, parts, ClaudePluginPartKind.Monitor, context.Qualified(monitorName), OneLine(Json.String(monitor["description"])), path.Length == 0 ? null : path);
    }

    private static JsonNode? ReadFileNode(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return !info.Exists || info.Length > JsonCap ? null : JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // The events of one hooks value: a file wraps them in "hooks", an inline object is the event map itself.
    private static void HookEvents(Context context, JsonObject events, string? path, List<ClaudePluginPart> parts)
    {
        foreach (var (@event, groups) in events)
        {
            if (groups is not JsonArray groupList) continue;
            foreach (var group in groupList.OfType<JsonObject>())
            {
                var matcher = Json.String(group["matcher"]) ?? "";
                foreach (var hook in (group["hooks"] as JsonArray ?? []).OfType<JsonObject>())
                    Add(context, parts, ClaudePluginPartKind.Hook, matcher.Length == 0 ? @event : $"{@event} · {matcher}", $"{Json.String(hook["type"]) ?? "command"} hook", path);
            }
        }
    }

    private static void Hooks(Context context, JsonNode? declared, List<ClaudePluginPart> parts)
    {
        void FromFile(string file)
        {
            if (ReadJson(file, JsonCap) is { } document && document["hooks"] is JsonObject events) HookEvents(context, events, file, parts);
        }
        if (Inside(context, Path.Combine("hooks", "hooks.json")) is { } defaultFile) FromFile(defaultFile);
        foreach (var item in Items(declared))
        {
            if (item is JsonObject inline) HookEvents(context, inline, context.InlinePath, parts);
            else if (Json.String(item) is { } relative && Inside(context, relative) is { } file) FromFile(file);
        }
    }

    // MCP and LSP servers share a shape: a file (or inline object) maps a server name to its settings. Only the name and
    // the transport or the file types leave this method.
    private static void Servers(Context context, JsonNode? declared, string defaultFile, string? wrapper, ClaudePluginPartKind kind, List<ClaudePluginPart> parts)
    {
        void Map(JsonObject map, string? path)
        {
            if (wrapper is not null && map[wrapper] is JsonObject wrapped) map = wrapped;
            foreach (var (server, settings) in map)
            {
                if (settings is not JsonObject config) continue;
                var description = kind == ClaudePluginPartKind.Mcp ? Transport(config) : FileTypes(config);
                Add(context, parts, kind, $"plugin:{context.Plugin}:{server}", description, path);
            }
        }
        if (Inside(context, defaultFile) is { } file && ReadJson(file, JsonCap) is { } document) Map(document, file);
        foreach (var item in Items(declared))
        {
            if (item is JsonObject inline) Map(inline, context.InlinePath);
            else if (Json.String(item) is { } relative && relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && Inside(context, relative) is { } declaredFile && ReadJson(declaredFile, JsonCap) is { } loaded) Map(loaded, declaredFile);
        }
    }

    private static string Transport(JsonObject config) =>
        Json.String(config["type"]) is { Length: > 0 } type ? type : config["url"] is not null ? "http" : "stdio";

    private static string? FileTypes(JsonObject config)
    {
        if (config["extensionToLanguage"] is not JsonObject map || map.Count == 0) return "language server";
        var extensions = map.Select(pair => pair.Key).Where(key => key.Length is > 0 and <= 16).Take(6).ToArray();
        return "language server for " + string.Join(", ", extensions) + (map.Count > extensions.Length ? ", …" : "");
    }
}