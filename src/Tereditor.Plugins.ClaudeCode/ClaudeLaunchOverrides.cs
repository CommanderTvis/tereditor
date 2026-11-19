namespace Tereditor.Plugins.ClaudeCode;

/// <summary>One Claude Code settings key that tereditor sets for a session it starts, how, and in which case.</summary>
/// <param name="Key">The settings key the value replaces, as the settings browser shows it.</param>
/// <param name="Flags">The launch words that carry it; the checks assert the launcher emits them.</param>
/// <param name="When">The case in which tereditor sets it.</param>
public sealed record ClaudeLaunchOverride(string Key, IReadOnlyList<string> Flags, string When);

/// <summary>
/// The one list of settings that tereditor overrides when it starts a Claude Code session. The settings browser marks these keys
/// and the checks compare the list with what the launcher really passes. Add a key here whenever a launch starts setting one.
/// A launch passes a value only in the case named; otherwise the file value applies.
/// </summary>
public static class ClaudeLaunchOverrides
{
    public const string Tooltip = "Tereditor sets this for sessions it starts. The value in this file is not used there.";

    public static readonly IReadOnlyList<ClaudeLaunchOverride> All =
    [
        new("model", ["--model"], "The Model setting, a model preset or the chat's model is set."),
        new("effortLevel", ["--effort"], "The Effort setting or the chat's effort is set."),
        new("permissions.defaultMode", ["--permission-mode"], "The Permission mode setting or the chat's mode is set, and the command has no permission flag of its own."),
        new("advisorModel", ["--advisor"], "The Advisor setting or the chat's advisor is set."),
        new("claudeInChromeDefaultEnabled", ["--chrome", "--no-chrome"], "The Chrome setting or the chat's Chrome choice is set."),
        // The model source writes a settings file passed with --settings; a cloud platform source sets variables instead.
        new("apiKeyHelper", ["--settings"], "A model source with an API key is chosen."),
        new("env", ["--settings"], "A model source is chosen: its address and token go in the generated settings file, or a cloud platform's variables are set.")
    ];

    /// <summary>The override for <paramref name="key"/> itself or for a key inside an overridden object (<c>env.NAME</c>); null when tereditor does not set it.</summary>
    public static ClaudeLaunchOverride? For(string key) =>
        All.FirstOrDefault(entry => key == entry.Key || key.StartsWith(entry.Key + ".", StringComparison.Ordinal));
}