using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.GitHub.Host;

/// <summary>Provides the GitHub repo hosting provider (H24): every <c>gh</c> call of the product runs here.</summary>
public sealed class GitHubHost : PluginHostModule
{
    public override PluginContract Contract => GitHubContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var provider = new GitHubProvider(context.RunToolAsync) { Log = message => context.Log.Warn(message) };
        context.RepoHost(provider);
        context.Method(GitHubContract.SignIn, async (_, _, cancellationToken) =>
        {
            var status = await provider.SignInAsync(cancellationToken);
            return new GitHubSignIn(status.Connected, status.Detail);
        });
        var cache = context.StateDirectory is { } state ? new GitHubCache(Path.Combine(state, "cache"), () => context.Settings<GitHubSettings>().KeepCache) : null;
        var browser = new GitHubBrowser(context.RunToolAsync, cache);
        async ValueTask<string> Directory(string workspaceId, CancellationToken cancellationToken) =>
            (await context.WorkspaceAsync(workspaceId, cancellationToken))?.Path ?? throw new InvalidOperationException("Unknown workspace: " + workspaceId);
        context.Method(GitHubContract.List, async (p, _, ct) => await browser.ListAsync(await Directory(p.WorkspaceId, ct), p, ct));
        context.Method(GitHubContract.Detail, async (p, _, ct) => await browser.DetailAsync(await Directory(p.WorkspaceId, ct), p, ct));
        context.Method(GitHubContract.View, async (p, _, ct) => await browser.ViewAsync(await Directory(p.WorkspaceId, ct), p, ct));
        context.Method(GitHubContract.ContributorStats, async (p, _, ct) => await browser.ContributorsAsync(await Directory(p.WorkspaceId, ct), p, ct));
        context.Method(GitHubContract.Post, async (p, _, ct) => await browser.PostAsync(await Directory(p.WorkspaceId, ct), p, ct));
        context.Method(GitHubContract.Log, async (p, _, ct) => await browser.LogAsync(await Directory(p.WorkspaceId, ct), p, ct));
        context.Method(GitHubContract.Info, async (p, _, ct) =>
            await browser.InfoAsync(await Directory(p.WorkspaceId, ct), Math.Clamp(context.Settings<GitHubSettings>().PollSeconds, 15, 600), ct));
        context.Method(GitHubContract.Cache, async (p, _, _) => new GitHubCacheSize(p.Clear ? await browser.ClearCacheAsync() : await browser.CacheSizeAsync()));
        return ValueTask.FromResult<PluginDisposer?>(null);
    }
}