using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Omp;

public static class OmpManifest
{
    public const string Id = "omp";
    public const string Icon = "asset:omp.svg";

    public static readonly PluginManifest Manifest = new(Id, "omp", Icon, "0.1.0", PluginApi.Generation, OmpContract.WireVersion)
    {
        Category = PluginCategories.Agents,
        // Runs the omp CLI as a process on the host's operating system, which a browser tab does not have.
        UnavailableOn = [HostKind.PureWeb],
        Description = "Runs the omp coding agent in the terminal with tereditor's MCP tools and shows its context files.",
        Support = PluginSupportLevels.Early,
        // omp takes the Anthropic, OpenAI Responses and OpenAI Chat APIs through its models file; its own sign-in stays.
        AgentRouting = new([
            new(ModelApiKinds.AnthropicMessages, AgentInjection.ModelsFile, [ModelAuthKind.ApiKey]),
            new(ModelApiKinds.OpenAiResponses, AgentInjection.ModelsFile, [ModelAuthKind.ApiKey]),
            new(ModelApiKinds.OpenAiChat, AgentInjection.ModelsFile, [ModelAuthKind.ApiKey])]),
        EnabledByDefault = false,
        Headless = HeadlessSupport.No,
        Host = "Tereditor.Plugins.Omp.Host.dll",
        Ui = "Tereditor.Plugins.Omp.UI.dll",
        Assets = "assets"
    };
}