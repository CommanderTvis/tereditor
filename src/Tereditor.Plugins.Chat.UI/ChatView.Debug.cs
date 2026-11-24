using Avalonia.Controls;
using Avalonia.Threading;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Chat.UI;

// The debug log: a panel over the transcript, offered from the "+" menu when the host keeps one (the header says so).
internal sealed partial class ChatView
{
    private ChatDebugPanel? debugPanel;
    private bool debugLoaded;

    internal ChatDebugPanel? DebugPanel => debugPanel;

    private void InitDebug(Grid body)
    {
        debugPanel = new ChatDebugPanel(env.Agent.Short);
        debugPanel.CloseRequested += () => debugPanel.IsVisible = false;
        debugPanel.ClearRequested += () => _ = ClearDebugAsync();
        body.Children.Add(debugPanel);
    }

    // The header says whether the host keeps a log; its older lines are fetched once, the rest arrive as pushes.
    private void OnDebugHeader(ChatHeader header)
    {
        if (debugPanel is null) return;
        if (!header.DebugLog)
        {
            debugPanel.IsVisible = false;
            debugLoaded = false;
            return;
        }
        if (debugLoaded) return;
        debugLoaded = true;
        _ = LoadDebugAsync();
    }

    private async Task LoadDebugAsync()
    {
        try
        {
            var log = await env.Client.RequestAsync(ChatContract.DebugLog, key);
            debugPanel?.Merge(log.Entries);
        }
        catch (Exception error) when (error is not OperationCanceledException) { debugLoaded = false; }
    }

    private async Task ClearDebugAsync()
    {
        try { await env.Client.RequestAsync(ChatContract.DebugClear, key); }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    /// <summary>The "+" menu's check row for the debug log; present only when the host keeps one.</summary>
    private void AddDebugRow(ContextMenu menu)
    {
        if (debugPanel is not { } panel || transcript.Header is not { DebugLog: true }) return;
        menu.Items.Add(ChatPopup.CheckItem("ChatDebugToggle", "Debug log", panel.IsVisible, () => Dispatcher.UIThread.Post(() => panel.IsVisible = !panel.IsVisible, DispatcherPriority.Background),
            tip: "Show the raw messages exchanged with " + env.Agent.Name));
    }
}