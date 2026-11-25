using Avalonia.Controls;
using Avalonia.Threading;

using Tereditor.Plugins.Agent.UI;
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
            Dispatcher.UIThread.Post(ShowDebugCount);
        }
        catch (Exception error) when (error is not OperationCanceledException) { debugLoaded = false; }
    }

    private async Task ClearDebugAsync()
    {
        try { await env.Client.RequestAsync(ChatContract.DebugClear, key); }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    /// <summary>The "+" menu's check row for the debug log, or the hint to restart when Debug mode is on and this chat has no log.</summary>
    private void AddDebugRow(ContextMenu menu)
    {
        if (debugPanel is not { } panel) return;
        if (transcript.Header is { DebugLog: true })
            menu.Items.Add(ChatPopup.CheckItem("ChatDebugToggle", "Debug log", panel.IsVisible, () => Dispatcher.UIThread.Post(() => panel.IsVisible = !panel.IsVisible, DispatcherPriority.Background),
                tip: "Show the raw messages exchanged with " + env.Agent.Name));
        else if (env.Agent.DebugModeOn())
            menu.Items.Add(ChatPopup.CheckItem("ChatDebugHint", "Debug log: " + DebugHint, false, () => { }, tip: DebugHint));
    }

    internal const string DebugHint = "Restart this chat to start the log";

    private IDisposable? debugWatch;
    private TextBlock? debugCount;

    // A change of the Debug mode setting shows or hides the chip and the hint at once.
    private void WatchDebug() => debugWatch ??= env.Agent.WatchDebugMode(() =>
    {
        chipSignature = "";
        if (transcript.Header is { } header) RebuildChips(header);
    });

    private void UnwatchDebug()
    {
        debugWatch?.Dispose();
        debugWatch = null;
    }

    private string DebugChipState(ChatHeader header) => !env.Agent.DebugModeOn() ? "" : header.DebugLog ? "log" : "hint";

    // The quiet chip beside the others: with a log it toggles the panel and counts the lines; without one (the chat started before Debug mode was turned on) it says what to do.
    private Control? DebugChip(ChatHeader header)
    {
        debugCount = null;
        if (debugPanel is not { } panel || !env.Agent.DebugModeOn()) return null;
        if (!header.DebugLog) return TerminalFacts.FactChip("debug", "Debug: restart chat", DebugHint);
        debugCount = new TextBlock { Text = panel.Total.ToString(), FontSize = 12, Foreground = Ui.Muted };
        return TerminalFacts.ActionFactChip("debug", "Debug", "Show the raw messages exchanged with " + env.Agent.Name, () => panel.IsVisible = !panel.IsVisible, debugCount);
    }

    private void ShowDebugCount()
    {
        if (debugCount is not null && debugPanel is { } panel) debugCount.Text = panel.Total.ToString();
    }
}