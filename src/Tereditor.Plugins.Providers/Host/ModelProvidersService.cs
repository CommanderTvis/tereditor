using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers.Host;

/// <summary>Where the model provider service keeps what is not a key: the user's custom providers and each agent's chosen source. Both hold no secret.</summary>
internal sealed record ModelProviderState(Func<IReadOnlyList<ProviderDefinition>> LoadCustom, Action<IReadOnlyList<ProviderDefinition>> SaveCustom,
    Func<IReadOnlyList<ModelSourceChoice>> LoadSources, Action<IReadOnlyList<ModelSourceChoice>> SaveSources);

/// <summary>
/// The merged list of model providers (bundled, contributed by plugins that depend on Model providers, and the user's own),
/// their keys in the <see cref="ProviderKeyStore"/>, and the model source chosen for each agent. A report never carries a key.
/// </summary>
internal sealed class ModelProvidersService(ProviderKeyStore keys, ModelProviderState state)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, IReadOnlyList<ProviderDefinition>> contributed = new(StringComparer.Ordinal);

    // Agent kinds a custom provider may declare; Vertex and Bedrock need more than a URL and a key, so they come from their own plugins.
    private static readonly string[] CustomKinds = [ModelApiKinds.AnthropicMessages, ModelApiKinds.OpenAiResponses, ModelApiKinds.OpenAiChat, ModelApiKinds.GoogleGenerative];

    public ModelProvidersReport Report()
    {
        lock (gate) return Build();
    }

    public ModelProvidersReport SaveKey(SaveModelKeyParams p)
    {
        lock (gate)
        {
            if (Merged().All(provider => provider.Id != p.Id)) throw new ArgumentException("No provider has that id.");
            keys.Save(p.Id, p.Key);
            return Build();
        }
    }

    public ModelProvidersReport RemoveKey(ModelProviderParams p)
    {
        lock (gate)
        {
            keys.Remove(p.Id);
            Prune();
            return Build();
        }
    }

    public ModelProvidersReport AddCustom(AddCustomProviderParams p)
    {
        lock (gate)
        {
            var name = p.Name.Trim();
            if (name.Length is 0 or > 64 || name.Any(char.IsControl)) throw new ArgumentException("Give the provider a name of up to 64 characters.");
            if (!Uri.TryCreate(p.BaseUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0)
                throw new ArgumentException("The base URL must start with http:// or https:// and carry no user name.");
            if (url.Scheme == "http" && !url.IsLoopback) throw new ArgumentException("A plain http:// address is allowed only on this computer. Use https:// for any other.");
            var kinds = p.Kinds.Distinct().ToArray();
            if (kinds.Length == 0 || kinds.Any(kind => !CustomKinds.Contains(kind))) throw new ArgumentException("Choose at least one API kind from the list.");
            var key = p.Key?.Trim() ?? "";
            if (key.Any(c => c is '\n' or '\r' or '\0')) throw new ArgumentException("A key is one line.");
            var taken = Merged().Select(provider => provider.Id).ToHashSet(StringComparer.Ordinal);
            var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-')]).Trim('-');
            var baseId = "custom-" + (slug.Length == 0 ? "provider" : slug.Length > 40 ? slug[..40] : slug);
            var id = baseId;
            for (var n = 2; taken.Contains(id); n++) id = baseId + "-" + n;
            var auth = new[] { ModelAuthKind.ApiKey };
            var definition = new ProviderDefinition(id, name, ProviderGroup.Other, [.. kinds.Select(kind => new ProviderApi(kind, url.ToString().TrimEnd('/'), auth))])
            {
                Custom = true,
                NeedsKey = key.Length > 0
            };
            if (key.Length > 0) keys.Save(id, key);
            state.SaveCustom([.. state.LoadCustom(), definition]);
            return Build();
        }
    }

    public ModelProvidersReport RemoveCustom(ModelProviderParams p)
    {
        lock (gate)
        {
            var custom = state.LoadCustom();
            if (custom.All(provider => provider.Id != p.Id)) return Build();
            state.SaveCustom([.. custom.Where(provider => provider.Id != p.Id)]);
            keys.Remove(p.Id);
            Prune();
            return Build();
        }
    }

    public ModelProvidersReport SetSource(SetModelSourceParams p)
    {
        lock (gate)
        {
            if (p.AgentId.Length is 0 or > 64 || !ProviderCatalog.IsProviderId(p.AgentId)) throw new ArgumentException("An agent id uses lower case letters, digits, dots, dashes and underscores.");
            var rest = state.LoadSources().Where(choice => choice.AgentId != p.AgentId).ToList();
            if (p.ProviderId is { } id)
            {
                var row = Rows().FirstOrDefault(candidate => candidate.Provider.Id == id) ?? throw new ArgumentException("No provider has that id.");
                if (!row.Ready) throw new ArgumentException("Add a key for this provider first.");
                rest.Add(new(p.AgentId, id));
            }
            state.SaveSources(rest);
            return Build();
        }
    }

    public ProvidersAck Contribute(ProviderContribution contribution)
    {
        if (!ProviderCatalog.IsProviderId(contribution.Contributor)) throw new ArgumentException("A contributor is a plugin id.");
        foreach (var provider in contribution.Providers)
        {
            if (!ProviderCatalog.IsProviderId(provider.Id)) throw new ArgumentException("A provider id uses lower case letters, digits, dots, dashes and underscores.");
            if (provider.Apis.Any(api => !ModelApiKinds.IsKnown(api.Kind) || !Uri.TryCreate(api.BaseUrl, UriKind.Absolute, out _))) throw new ArgumentException("A provider API needs a known kind and an absolute base URL.");
        }
        lock (gate)
        {
            contributed[contribution.Contributor] = [.. contribution.Providers.Select(provider => provider with { Contributor = contribution.Contributor, Custom = false })];
            return new();
        }
    }

    public ProvidersAck Withdraw(WithdrawParams p)
    {
        lock (gate)
        {
            if (contributed.Remove(p.Contributor)) Prune();
            return new();
        }
    }

    // Bundled first, then contributed, then the user's own; the first provider with an id keeps it.
    private List<ProviderDefinition> Merged()
    {
        var all = new List<ProviderDefinition>(BundledProviders.All);
        foreach (var provider in contributed.Values.SelectMany(list => list).Concat(state.LoadCustom().Select(custom => custom with { Custom = true, Contributor = null })))
            if (all.All(existing => existing.Id != provider.Id)) all.Add(provider);
        return all;
    }

    private List<ModelProviderRow> Rows() => [.. Merged().Select(provider => new ModelProviderRow(provider, !provider.NeedsKey || keys.Has(provider.Id)))];

    // A source whose provider is gone or has lost its key is dropped, so an agent falls back to its own provider.
    private void Prune()
    {
        var ready = Rows().Where(row => row.Ready).Select(row => row.Provider.Id).ToHashSet(StringComparer.Ordinal);
        var sources = state.LoadSources();
        var kept = sources.Where(choice => ready.Contains(choice.ProviderId)).ToList();
        if (kept.Count != sources.Count) state.SaveSources(kept);
    }

    private ModelProvidersReport Build() => new(Rows(), state.LoadSources()) { Helper = keys.HelperPath };
}