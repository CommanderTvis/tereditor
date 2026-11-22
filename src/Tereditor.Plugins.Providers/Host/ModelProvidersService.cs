using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers.Host;

/// <summary>Where the model provider service keeps what is not a key: the user's custom providers and each agent's chosen source. Both hold no secret.</summary>
internal sealed record ModelProviderState(Func<IReadOnlyList<ProviderDefinition>> LoadCustom, Action<IReadOnlyList<ProviderDefinition>> SaveCustom,
    Func<IReadOnlyList<ModelSourceChoice>> LoadSources, Action<IReadOnlyList<ModelSourceChoice>> SaveSources);

/// <summary>
/// The merged list of model providers (bundled, contributed by plugins that depend on Model providers, and the user's own),
/// their keys in the <see cref="ProviderKeyStore"/>, and the model source chosen for each agent. A report never carries a key.
/// </summary>
internal sealed class ModelProvidersService(ProviderKeyStore keys, ModelProviderState state, AgentModelFiles? files = null, Func<CancellationToken, Task<ProviderSignIn>>? readSignIn = null)
{
    // The status of OpenAI's own sign-in as last read; null until a read. A snapshot swapped whole, never a token.
    private ProviderSignIn? openAiSignIn;

    private readonly Lock gate = new();
    // The agents whose models file is written now. Read by launches, so it is a snapshot swapped whole and never needs a lock or any I/O.
    private volatile IReadOnlySet<string> active = new HashSet<string>();
    private readonly Dictionary<string, IReadOnlyList<ProviderDefinition>> contributed = new(StringComparer.Ordinal);

    // Agent kinds a custom provider may declare; Vertex and Bedrock need more than a URL and a key, so they come from their own plugins.
    private static readonly string[] CustomKinds = [ModelApiKinds.AnthropicMessages, ModelApiKinds.OpenAiResponses, ModelApiKinds.OpenAiChat, ModelApiKinds.GoogleGenerative];

    public ModelProvidersReport Report()
    {
        lock (gate) return Build();
    }

    /// <summary>Reads the provider's own sign-in again. Only OpenAI has one; any other id reads nothing. Runs a process, so callers keep it off any UI thread.</summary>
    public async Task<ModelProvidersReport> RefreshSignInAsync(ModelProviderParams p, CancellationToken cancel = default)
    {
        if (p.Id == "openai" && readSignIn is not null)
        {
            var next = await readSignIn(cancel);
            lock (gate) openAiSignIn = next;
        }
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
            var written = false;
            if (p.ProviderId is { } id)
            {
                var row = Rows().FirstOrDefault(candidate => candidate.Provider.Id == id) ?? throw new ArgumentException("No provider has that id.");
                if (!row.Ready) throw new ArgumentException("Add a key for this provider first.");
                var wired = row.Provider.WiredAgents.Contains(p.AgentId);
                if (row.Provider.Apis.Count == 0 && !wired) throw new ArgumentException("This provider does not work with that agent.");
                // The file comes first: a source that could not be written is never recorded, so the agent keeps its own provider.
                // A wired provider writes none: its contributor starts the agent, and an old file is removed below.
                if (files is not null && AgentModelFiles.Handles(p.AgentId) && !wired)
                {
                    files.Write(p.AgentId, row.Provider, p.Model);
                    written = true;
                }
                rest.Add(new(p.AgentId, id) { Model = p.Model });
            }
            state.SaveSources(rest);
            if (files is not null && AgentModelFiles.Handles(p.AgentId))
            {
                if (!written) files.Remove(p.AgentId);
                SetActive(p.AgentId, written);
            }
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

    private List<ModelProviderRow> Rows() => [.. Merged().Select(provider => new ModelProviderRow(provider, !provider.NeedsKey || keys.Has(provider.Id)) { SignIn = provider.Id == "openai" ? openAiSignIn : null })];

    // A source whose provider is gone or has lost its key is dropped, so an agent falls back to its own provider.
    private void Prune()
    {
        var ready = Rows().Where(row => row.Ready).Select(row => row.Provider.Id).ToHashSet(StringComparer.Ordinal);
        var sources = state.LoadSources();
        var kept = sources.Where(choice => ready.Contains(choice.ProviderId)).ToList();
        if (kept.Count == sources.Count) return;
        state.SaveSources(kept);
        if (files is null) return;
        foreach (var gone in sources.Where(choice => !kept.Contains(choice) && AgentModelFiles.Handles(choice.AgentId)))
        {
            files.Remove(gone.AgentId);
            SetActive(gone.AgentId, false);
        }
    }

    private void SetActive(string agentId, bool on)
    {
        var next = new HashSet<string>(active);
        if (on) next.Add(agentId); else next.Remove(agentId);
        active = next;
    }

    /// <summary>
    /// Writes the models file of every agent with a chosen source, at startup, so a moved state directory or a changed helper path
    /// is followed. A source whose file cannot be written, or whose key is gone, is dropped, so the agent keeps its own provider and the
    /// recorded sources always match what launches use.
    /// </summary>
    public void Sync()
    {
        if (files is null) return;
        lock (gate)
        {
            var rows = Rows();
            var next = new HashSet<string>();
            var all = state.LoadSources();
            var kept = new List<ModelSourceChoice>();
            foreach (var choice in all)
            {
                if (!AgentModelFiles.Handles(choice.AgentId)) { kept.Add(choice); continue; }
                var found = rows.FirstOrDefault(row => row.Provider.Id == choice.ProviderId);
                // A provider whose contributor has not started yet (it starts after this plugin) keeps its choice.
                if (found is null && choice.ProviderId == ProviderCatalog.JetBrainsAi) { kept.Add(choice); continue; }
                if (found is { Ready: true } wiredRow && wiredRow.Provider.WiredAgents.Contains(choice.AgentId))
                {
                    try { files.Remove(choice.AgentId); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    kept.Add(choice);
                    continue;
                }
                if (found is { Ready: true } row)
                {
                    try
                    {
                        files.Write(choice.AgentId, row.Provider, choice.Model);
                        next.Add(choice.AgentId);
                        kept.Add(choice);
                        continue;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
                try { files.Remove(choice.AgentId); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            if (kept.Count != all.Count) state.SaveSources(kept);
            active = next;
        }
    }

    /// <summary>
    /// What an agent's launch takes from its chosen provider, or null when it has none or its file is not in place: Pi runs in the agent
    /// directory that holds the models file, omp in its <c>tereditor</c> profile. Neither carries a key; the file asks the helper for it.
    /// </summary>
    public AgentLaunchContribution? Launch(string agentId)
    {
        if (files is null || !active.Contains(agentId)) return null;
        return agentId switch
        {
            AgentModelFiles.Pi => new() { Environment = new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = files.PiAgentDirectory } },
            AgentModelFiles.Omp => ProviderCatalog.OmpLaunch,
            _ => null
        };
    }

    private ModelProvidersReport Build() => new(Rows(), state.LoadSources()) { Helper = keys.HelperPath };
}