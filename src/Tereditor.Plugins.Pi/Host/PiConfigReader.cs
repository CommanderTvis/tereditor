using System.Text.Json;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>Where Pi's configuration is read from: the agent directory, the home directory and the working directory.</summary>
internal sealed record PiConfigRoots(string AgentDir, string Home, string Cwd);

/// <summary>
/// Reads what a Pi session in a directory would load, from the files Pi 1.6.0 reads (docs/configuration.md, skills.md, mcp.md,
/// packages.md, security.md). It only reads: no Pi process runs and no file is written. Project resources load only after the
/// project is trusted (trust.json in the agent directory); context files load regardless of trust.
/// </summary>
internal static class PiConfigReader
{
    // The order Pi tries inside one directory: the first file that exists is the directory's context file.
    private static readonly string[] ContextNames = ["AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"];

    private const string TrustNone = "not decided", TrustYes = "trusted", TrustNo = "not trusted";

    /// <summary>The agent directory a chat would use: a model source's directory, then PI_CODING_AGENT_DIR, then ~/.pi/agent.</summary>
    public static string AgentDir(IReadOnlyDictionary<string, string> contributed, string? environment, string home) =>
        contributed.TryGetValue("PI_CODING_AGENT_DIR", out var agent) && agent.Length > 0 ? agent
        : environment is { Length: > 0 } fromEnvironment ? fromEnvironment
        : Path.Combine(home, ".pi", "agent");

    public static PiConfigSnapshot Read(PiConfigRoots roots, IReadOnlyList<PiConfigCommand> commands, bool chatOpen)
    {
        var cwd = Path.GetFullPath(roots.Cwd);
        var trustFile = Path.Combine(roots.AgentDir, "trust.json");
        var decision = NearestDecision(ReadTrust(trustFile), cwd);
        var trusted = decision == true;
        var trust = decision switch { true => TrustYes, false => TrustNo, _ => TrustNone };
        var repo = RepoRoot(cwd) ?? cwd;
        var projectDir = Path.Combine(cwd, ".pi");
        var items = new List<PiConfigItem>();

        // Context files: the agent directory, then the working directory's ancestors from the top down to the working directory.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[] { roots.AgentDir }.Concat(Ancestors(cwd).Reverse()))
        {
            if (!seen.Add(dir)) continue;
            var scope = dir == roots.AgentDir || !IsWithin(dir, repo) ? PiScopes.User : PiScopes.Project;
            if (ContextFileIn(dir) is { } file) items.Add(new(PiConfigKinds.Context, scope, Path.GetFileName(file), file, true, ""));
        }

        // Extensions, prompt templates and MCP servers: user files always load; project files only after trust.
        AddExtensions(items, Path.Combine(roots.AgentDir, "extensions"), PiScopes.User, true);
        AddExtensions(items, Path.Combine(projectDir, "extensions"), PiScopes.Project, trusted, trust);
        AddPrompts(items, Path.Combine(roots.AgentDir, "prompts"), PiScopes.User, true);
        AddPrompts(items, Path.Combine(projectDir, "prompts"), PiScopes.Project, trusted, trust);
        AddMcp(items, Path.Combine(roots.AgentDir, "mcp.json"), PiScopes.User, true);
        AddMcp(items, Path.Combine(projectDir, "mcp.json"), PiScopes.Project, trusted, trust);

        // Skills: Pi's own folders, then the Agent Skills folders (~/.agents/skills, and .agents/skills up to the repository root).
        var skillRoots = new List<(string Dir, string Scope, bool Loaded)>
        {
            (Path.Combine(roots.AgentDir, "skills"), PiScopes.User, true),
            (Path.Combine(roots.Home, ".agents", "skills"), PiScopes.User, true),
            (Path.Combine(projectDir, "skills"), PiScopes.Project, trusted)
        };
        skillRoots.AddRange(Ancestors(cwd).Where(dir => IsWithin(dir, repo)).Select(dir => (Path.Combine(dir, ".agents", "skills"), PiScopes.Project, trusted)));
        var seenSkills = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dir, scope, loaded) in skillRoots)
            foreach (var file in SkillFiles(dir).Where(file => seenSkills.Add(file)))
                items.Add(new(PiConfigKinds.Skill, scope, Path.GetFileName(Path.GetDirectoryName(file)) ?? "", file, loaded, loaded ? "" : NotLoaded(trust)));

        // Packages and settings: user settings always apply; project settings only after trust.
        var settings = new List<PiConfigSetting>();
        AddSettings(items, settings, Path.Combine(roots.AgentDir, "settings.json"), PiScopes.User, true, trust);
        AddSettings(items, settings, Path.Combine(projectDir, "settings.json"), PiScopes.Project, trusted, trust);

        return new PiConfigSnapshot(cwd, roots.AgentDir, trust, trustFile, items, settings, commands, chatOpen);
    }

    internal static string NotLoaded(string trust) =>
        trust == TrustNo ? "Not loaded: this project is not trusted." : "Not loaded until the project is trusted with /trust in Pi.";

    private static void AddExtensions(List<PiConfigItem> items, string dir, string scope, bool loaded, string trust = TrustYes)
    {
        foreach (var entry in Entries(dir))
        {
            if (File.Exists(entry) && (entry.EndsWith(".ts", StringComparison.Ordinal) || entry.EndsWith(".js", StringComparison.Ordinal)))
                items.Add(new(PiConfigKinds.Extension, scope, Path.GetFileName(entry), entry, loaded, loaded ? "" : NotLoaded(trust)));
            else if (Directory.Exists(entry) && EntryFile(entry) is { } file)
                items.Add(new(PiConfigKinds.Extension, scope, Path.GetFileName(entry), file, loaded, loaded ? "" : NotLoaded(trust)));
        }
    }

    private static void AddPrompts(List<PiConfigItem> items, string dir, string scope, bool loaded, string trust = TrustYes)
    {
        foreach (var entry in Entries(dir).Where(entry => File.Exists(entry) && entry.EndsWith(".md", StringComparison.Ordinal)))
            items.Add(new(PiConfigKinds.Prompt, scope, Path.GetFileNameWithoutExtension(entry), entry, loaded, loaded ? "" : NotLoaded(trust)));
    }

    private static void AddMcp(List<PiConfigItem> items, string file, string scope, bool loaded, string trust = TrustYes)
    {
        if (!File.Exists(file)) return;
        if (!loaded) { items.Add(new(PiConfigKinds.Mcp, scope, Path.GetFileName(file), file, false, NotLoaded(trust))); return; }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
                foreach (var server in servers.EnumerateObject())
                    items.Add(new(PiConfigKinds.Mcp, scope, server.Name, file, true, ""));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            items.Add(new(PiConfigKinds.Mcp, scope, Path.GetFileName(file), file, true, "Could not read this file: " + error.Message));
        }
    }

    private static void AddSettings(List<PiConfigItem> items, List<PiConfigSetting> settings, string file, string scope, bool loaded, string trust)
    {
        if (!File.Exists(file)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "packages")
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                        foreach (var package in property.Value.EnumerateArray())
                            if (PackageSource(package) is { Length: > 0 } source)
                                items.Add(new(PiConfigKinds.Package, scope, source, file, loaded, loaded ? "" : NotLoaded(trust)));
                    continue;
                }
                var text = property.Value.GetRawText();
                settings.Add(new(property.Name, scope, text.Length > 120 ? text[..120] + "…" : text, loaded));
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            settings.Add(new("settings.json", scope, "Could not read this file: " + error.Message, false));
        }
    }

    private static string PackageSource(JsonElement package) => package.ValueKind switch
    {
        JsonValueKind.String => package.GetString() ?? "",
        JsonValueKind.Object when package.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String => source.GetString() ?? "",
        _ => ""
    };

    // An extension folder loads from its index file or its package.json manifest; the row opens that file.
    private static string? EntryFile(string dir) =>
        new[] { "index.ts", "index.js", "package.json" }.Select(name => Path.Combine(dir, name)).FirstOrDefault(File.Exists);

    private static IEnumerable<string> SkillFiles(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseSensitive, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(dir, "SKILL.md", options).OrderBy(file => file, StringComparer.Ordinal);
    }

    private static string? ContextFileIn(string dir)
    {
        var names = Entries(dir).Where(File.Exists).Select(entry => Path.GetFileName(entry)).ToHashSet(StringComparer.Ordinal);
        return ContextNames.FirstOrDefault(names.Contains) is { } name ? Path.Combine(dir, name) : null;
    }

    private static IEnumerable<string> Entries(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        try { return Directory.EnumerateFileSystemEntries(dir).OrderBy(entry => entry, StringComparer.Ordinal).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    // The working directory and each parent, nearest first.
    private static IEnumerable<string> Ancestors(string cwd)
    {
        for (string? dir = cwd; dir is not null; dir = Path.GetDirectoryName(dir)) yield return dir;
    }

    private static string? RepoRoot(string cwd) =>
        Ancestors(cwd).FirstOrDefault(dir => Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")));

    private static bool IsWithin(string dir, string root) =>
        dir.Equals(root, StringComparison.Ordinal) || dir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>trust.json maps a canonical directory to true (trusted) or false (declined). Only booleans count, as Pi reads them.</summary>
    private static Dictionary<string, bool> ReadTrust(string file)
    {
        var decisions = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!File.Exists(file)) return decisions;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return decisions;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) decisions[property.Name] = property.Value.GetBoolean();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
        return decisions;
    }

    // The closest decision for the working directory or one of its parents applies, as Pi's trust store does.
    private static bool? NearestDecision(IReadOnlyDictionary<string, bool> decisions, string cwd)
    {
        foreach (var dir in Ancestors(cwd))
            if (decisions.TryGetValue(dir, out var decision)) return decision;
        return null;
    }
}