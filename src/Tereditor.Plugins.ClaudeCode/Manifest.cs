using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.ClaudeCode;

public static class ClaudeCodeManifest
{
    public static readonly PluginManifest Manifest = new(ClaudeCodeContract.Id, "Claude Code", "asset:claude.svg", "0.2.0", PluginApi.Generation, 3)
    {
        Category = PluginCategories.Agents,
        // Runs the claude CLI as a process on the host's operating system, which a browser tab does not have.
        UnavailableOn = [HostKind.PureWeb],
        Description = "Runs Claude Code in the terminal with an IDE bridge and live status.",
        // Claude Code takes the Anthropic Messages API, injected through a settings file; its own sign-in stays. It also runs on Amazon Bedrock and
        // Google Vertex AI (CLAUDE_CODE_USE_BEDROCK, CLAUDE_CODE_USE_VERTEX) with the platform's own credentials, set through the launch environment.
        AgentRouting = new([
            new(ModelApiKinds.AnthropicMessages, AgentInjection.SettingsFile, [ModelAuthKind.ApiKey]),
            new(ModelApiKinds.Bedrock, AgentInjection.Environment, [ModelAuthKind.Platform]),
            new(ModelApiKinds.Vertex, AgentInjection.Environment, [ModelAuthKind.Platform])]),
        EnabledByDefault = false,
        Headless = HeadlessSupport.No,
        Assets = "assets"
    };
}