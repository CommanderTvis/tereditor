using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Providers;

namespace Tereditor.Checks;

/// <summary>The provider each agent's Account names: the chosen Model source's name, or none (the built-in sign-in) when no source is chosen.</summary>
internal static class AccountProviderChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("account providers: " + message);
    }

    internal static void Run()
    {
        var rows = BundledProviders.All.Select(provider => new ModelProviderRow(provider, true)).ToList();
        ModelProvidersReport Chosen(string agent, string provider) => new(rows, [new ModelSourceChoice(agent, provider)]) { Helper = "/fixture/provider-key" };
        var none = new ModelProvidersReport(rows, []) { Helper = "/fixture/provider-key" };

        // Claude Code: a chosen source names the provider; with none, the launch contributes nothing and the account is internal.
        var claude = AgentLaunchComposition.ProviderName([ProviderLaunch.For("claude", Chosen("claude-code", "anthropic"))!]);
        Require(claude == "Anthropic API", "Claude Code names the chosen provider, Anthropic API.");
        Require(AgentLaunchComposition.ProviderName([.. new[] { ProviderLaunch.For("claude", none) }.OfType<AgentLaunchContribution>()]) is null, "Claude Code with no source is internal (no provider name).");

        // Codex: the same rule through its own launch seam.
        var codex = AgentLaunchComposition.ProviderName([ProviderLaunch.For("codex", Chosen("codex", "openrouter"))!]);
        Require(codex == "OpenRouter", "Codex names the chosen provider, OpenRouter.");
        Require(AgentLaunchComposition.ProviderName([.. new[] { ProviderLaunch.For("codex", none) }.OfType<AgentLaunchContribution>()]) is null, "Codex with no source uses its built-in sign-in.");

        // A source that cannot be used is still named, so the account does not claim the built-in sign-in.
        var blocked = ProviderLaunch.For("claude", new ModelProvidersReport(rows.Select(row => row with { Ready = false }).ToList(), [new ModelSourceChoice("claude-code", "anthropic")]));
        Require(blocked is { BlockedReason: not null } && blocked.Provider == "Anthropic API", "A blocked source still names its provider.");
    }
}