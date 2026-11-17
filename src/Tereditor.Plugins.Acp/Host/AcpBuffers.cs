using System.Collections.Concurrent;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>
/// Asks the editor, through the plugin's UI half, for the unsaved text of a file. The host cannot see the editor's documents, so it
/// pushes a request to the client that opened the chat and waits for the reply. No client, no answer in time or a failure means
/// "no unsaved text": the read falls back to the file on disk.
/// </summary>
internal sealed class AcpBuffers(IPluginHostContext context)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, PluginCall> clients = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> pending = new();

    /// <summary>Remembers the client that shows a workspace's chats; it answers that workspace's requests.</summary>
    public void Client(string workspaceId, PluginCall call) => clients[workspaceId] = call;

    /// <summary>The unsaved text of a workspace-relative file, or null.</summary>
    public async Task<string?> UnsavedAsync(string workspaceId, string relativePath, CancellationToken cancellationToken)
    {
        if (!clients.TryGetValue(workspaceId, out var client)) return null;
        var id = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        try
        {
            context.Publish(AcpContract.Buffer, new AcpBufferRequest(id, workspaceId, relativePath), client);
            return await reply.Task.WaitAsync(Timeout, cancellationToken);
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException) { return null; }
        finally { pending.TryRemove(id, out _); }
    }

    /// <summary>Settles a request with the editor's answer. Unknown and late ids are dropped.</summary>
    public void Settle(AcpBufferReply reply)
    {
        if (pending.TryRemove(reply.Id, out var entry)) entry.TrySetResult(reply.Text);
    }
}