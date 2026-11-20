using Tereditor.Host.Abstractions;
using Tereditor.Host.Core.Plugins;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.CodeIntelligence;
using Tereditor.Plugins.CodeIntelligence.Host;

namespace Tereditor.Checks;

/// <summary>The Code Intelligence host half answering from an E2E workspace's fake host, so a check scripts the replies of the page.</summary>
internal static class FakeCodeIntelligence
{
    internal static IReadOnlyList<(PluginManifest, PluginHostModule?)> HostPlugins(Func<IProjectServices> host, TimeSpan? termsDeadline = null) =>
    [
        (CodeIntelligenceContract.Manifest, new CodeIntelligenceHost
        {
            TermsReadDeadline = termsDeadline ?? TimeSpan.FromSeconds(90),
            LanguageCall = async (operation, request, token) => PluginHostContext.LanguageResultJson(await host().LanguageAsync(new(request.Workspace, Operation: operation, Server: request.Server), token)),
            TermsCall = async (request, token) => PluginHostContext.TermsResultJson(await host().LanguageTermsAsync(
                new LanguageTermsRequest(request.Workspace, request.Server, request.Accept, request.ArtifactId, request.EulaSha256) { Answers = request.Answers ?? new Dictionary<string, string>() }, token))
        })
    ];
}