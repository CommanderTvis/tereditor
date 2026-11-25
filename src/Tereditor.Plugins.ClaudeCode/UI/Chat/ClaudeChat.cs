using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Chat.UI;

namespace Tereditor.Plugins.ClaudeCode.UI.Chat;

/// <summary>What the shared chat view needs to know about Claude Code: its words, document and permission modes.</summary>
internal static class ClaudeChat
{
    public const string DocumentScheme = "claude-chat";

    /// <summary>The agent description; the plugin context asks the host for the CLI's permission modes.</summary>
    public static ChatAgent Agent(IPluginUIContext context) => Agent(() => PermissionModeSource.RefreshAsync(context)) with
    {
        DebugModeOn = () => context.Settings<ClaudeCodeSettings>().DebugMode,
        WatchDebugMode = changed => context.OnSettings<ClaudeCodeSettings>(_ => changed())
    };

    public static ChatAgent Agent(Func<Task<PermissionModesResult>> permissionModes) => new("Claude Code", "Claude", DocumentScheme, "asset:claude.svg")
    {
        TerminalKind = "claude",
        HelpUrl = "https://code.claude.com/docs",
        UnavailableHint = ", or switch to Terminal mode in Settings.",
        EffortLevels = ClaudeValues.EffortLevels,
        DescribeMode = id => Mode(ClaudePermissionModes.Describe(id)),
        Modes = async () => [.. (await permissionModes()).Modes.Select(id => Mode(ClaudePermissionModes.Describe(id)))]
    };

    private static ChatMode Mode(ClaudePermissionMode mode) => new(mode.Id, mode.Label, mode.Description) { Dangerous = mode.Dangerous };
}