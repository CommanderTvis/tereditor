using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Agent.Host;

/// <summary>The host methods behind an agent's Download, Cancel and Remove buttons.</summary>
internal static class ManagedDownloads
{
    /// <summary>
    /// Serves the three requests, each answering with <paramref name="status"/>, and deletes the download when the user turns the plugin off.
    /// <paramref name="install"/> is the only request that downloads; <paramref name="installed"/> runs after it, whether it worked or not.
    /// </summary>
    public static void Register<TParams, TStatus>(IPluginHostContext context, ManagedBunPackage managed, PluginMethod<TParams, TStatus> install,
        PluginMethod<TParams, TStatus> cancel, PluginMethod<TParams, TStatus> remove, Func<CancellationToken, Task<TStatus>> status, Action? installed = null)
    {
        context.Method(install, async (_, _, token) =>
        {
            try { await managed.EnsureAsync("", token); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException) { context.Log.Debug(managed.Name.ToLowerInvariant() + " download: " + error.Message); }
            if (installed is not null) await Task.Run(installed, CancellationToken.None);
            return await status(CancellationToken.None);
        });
        context.Method(cancel, async (_, _, token) =>
        {
            managed.Cancel();
            return await status(token);
        });
        context.Method(remove, async (_, _, token) =>
        {
            await Task.Run(managed.Remove, token);
            return await status(token);
        });
        // Turning the plugin off deletes what it downloaded.
        context.OnUserDisabled(() =>
        {
            managed.Remove();
            return ValueTask.CompletedTask;
        });
    }
}