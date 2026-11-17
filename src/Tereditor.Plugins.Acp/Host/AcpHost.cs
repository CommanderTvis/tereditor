using System.Text.Json.Serialization.Metadata;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>The ACP plugin's host half: the agent list and the chat sessions over ACP agents' stdio.</summary>
public sealed class AcpHost : PluginHostModule
{
    /// <summary>Where the registry copy and installed agents live; the user's .tereditor/acp unless a check says otherwise.</summary>
    internal string? DataDirectory { get; init; }

    public override PluginContract Contract => AcpContract.Contract;

    public override IJsonTypeInfoResolver Json => AcpHostJson.Default;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var chats = DataDirectory is { } directory ? new AcpChatHost(context) { DataDirectory = directory } : new AcpChatHost(context);
        chats.Register();
        // Turning ACP off deletes what it downloaded; quitting does not. The disposer has already ended the chats, so no agent runs from these folders.
        context.OnUserDisabled(() =>
        {
            PluginDownloads.Purge(chats.Downloads);
            return ValueTask.CompletedTask;
        });
        return ValueTask.FromResult<PluginDisposer?>(() => new ValueTask(chats.DisposeAsync()));
    }
}