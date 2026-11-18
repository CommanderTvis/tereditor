using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Runtimes.Host;

/// <summary>
/// The host half: the state of Bun and uv, Download now and Remove download. The resolution itself is the host's (<see cref="IPluginHostContext.ResolveRuntimeAsync"/>)
/// and the plugin's switch is what allows it to download. Turned off by the user, it stops what runs from its downloads and deletes all of them; quitting does not.
/// </summary>
public sealed class RuntimesHost : PluginHostModule
{
    public override PluginContract Contract => RuntimesContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.Method(RuntimesContract.Status, async (_, _, token) => await StateAsync(context, "", token));
        var broker = new RuntimeConsentBroker(consents => context.Publish(RuntimesContract.ConsentChanged, consents));
        context.Method(RuntimesContract.Consents, (_, _, _) => ValueTask.FromResult(broker.Snapshot()));
        context.Method(RuntimesContract.Answer, (parameters, _, _) => ValueTask.FromResult(broker.Answer(Parse(parameters.Kind), parameters.Download)));
        context.SetRuntimeConsent(broker.AskAsync);
        context.Method(RuntimesContract.Download, async (parameters, _, token) =>
        {
            var kind = Parse(parameters.Kind);
            broker.Forget(kind);
            string error = "";
            try { await Task.Run(() => context.DownloadRuntimeAsync(kind, token).AsTask(), token); }
            catch (Exception failure) when (failure is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException)
            {
                error = (kind == RuntimeKind.Bun ? "Bun" : "uv") + " could not be downloaded: " + failure.Message;
            }
            return await StateAsync(context, error, token);
        });
        context.Method(RuntimesContract.Remove, async (parameters, _, token) =>
        {
            await Task.Run(() => context.RemoveRuntimeDownloadAsync(Parse(parameters.Kind), token).AsTask(), token);
            return await StateAsync(context, "", token);
        });
        context.OnUserDisabled(() => context.PurgeRuntimeDownloadsAsync());
        return ValueTask.FromResult<PluginDisposer?>(() =>
        {
            context.SetRuntimeConsent(null);
            broker.Close();
            return ValueTask.CompletedTask;
        });
    }

    private static RuntimeKind Parse(string kind) => kind switch
    {
        "bun" => RuntimeKind.Bun,
        "uv" => RuntimeKind.Uv,
        _ => throw new ArgumentException("Unknown runtime: " + kind)
    };

    private static async ValueTask<RuntimesState> StateAsync(IPluginHostContext context, string error, CancellationToken token) =>
        // The embedded host runs a handler on the caller's thread, and the status walks the cache folders.
        new([.. (await Task.Run(() => context.RuntimeStatusAsync(token).AsTask(), token)).Select(status => new RuntimeRow(status.Kind == RuntimeKind.Bun ? "bun" : "uv", status.Name,
            status.Source switch { RuntimeSource.Pinned => "pinned", RuntimeSource.System => "system", RuntimeSource.Downloaded => "downloaded", _ => "" },
            status.Path, status.Version, status.Pinned, status.DownloadBytes, status.Downloaded, status.Message) { DownloadedPath = status.DownloadedPath })], error);
}