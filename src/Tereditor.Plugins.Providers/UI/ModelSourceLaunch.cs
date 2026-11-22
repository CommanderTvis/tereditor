using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers.UI;

/// <summary>
/// What the UI half contributes to an agent's launch line: omp starts in tereditor's profile while a source is chosen for it. It reads the sources
/// the pages last showed, in memory only, so building a launch line never waits on the host. The host half contributes the same for a revive line.
/// </summary>
internal sealed class ModelSourceLaunch
{
    private volatile IReadOnlyList<ModelSourceChoice> sources = [];

    public void Update(ModelProvidersReport report) => sources = report.Sources;

    /// <summary>Raised when the host pushes a report (a key saved, a sign-in read), so an open page shows it without being asked.</summary>
    public event Action<ModelProvidersReport>? Reported;

    public void Report(ModelProvidersReport report)
    {
        Update(report);
        Reported?.Invoke(report);
    }

    public AgentLaunchContribution? For(string agentId) =>
        agentId == ProviderCatalog.OmpAgent && sources.Any(choice => choice.AgentId == agentId) ? ProviderCatalog.OmpLaunch : null;
}