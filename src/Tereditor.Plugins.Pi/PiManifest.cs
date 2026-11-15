using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Pi;

public static class PiManifest
{
    public const string Id = "pi";
    public const string Icon = "asset:pi.svg";

    public static readonly PluginManifest Manifest = new(Id, "Pi", Icon, "0.1.0", PluginApi.Generation, PiContract.WireVersion)
    {
        Category = PluginCategories.Agents,
        Description = "Chats with the Pi coding agent over its RPC mode, with the model providers Pi is signed in to.",
        Support = PluginSupportLevels.Early,
        // Pi takes the Anthropic, OpenAI Responses and OpenAI Chat APIs through its models file; its own sign-in stays.
        AgentRouting = new([
            new(ModelApiKinds.AnthropicMessages, AgentInjection.ModelsFile, [ModelAuthKind.ApiKey, ModelAuthKind.OAuth]),
            new(ModelApiKinds.OpenAiResponses, AgentInjection.ModelsFile, [ModelAuthKind.ApiKey, ModelAuthKind.OAuth]),
            new(ModelApiKinds.OpenAiChat, AgentInjection.ModelsFile, [ModelAuthKind.ApiKey, ModelAuthKind.OAuth])]),
        EnabledByDefault = false,
        Headless = HeadlessSupport.No,
        Host = "Tereditor.Plugins.Pi.Host.dll",
        Ui = "Tereditor.Plugins.Pi.UI.dll",
        Assets = "assets",
        Contributes = new() { SideTools = [new("pane", "Pi", Icon, PluginToolSide.Right)] }
    };
}