using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Chat.UI;

/// <summary>Where a chat tab's mark reads what its chat is doing, and what tells it that changed.</summary>
internal interface IChatActivitySource
{
    ChatActivity Of(string workspaceId, string chatId);

    /// <summary>Raised on the UI thread with the workspace and chat whose activity changed.</summary>
    event Action<string, string>? Changed;
}

/// <summary>
/// What every chat of the workspaces a rail shows is doing, as the host pushes it on <see cref="ChatContract.Activity"/>: the chat view
/// subscribes only while its tab is open, and a tab that is not open still wears its chat's state. Lives on the UI thread.
/// </summary>
internal sealed class ChatActivityTracker : IChatActivitySource, IDisposable
{
    private readonly IPluginUIContext context;
    private readonly Dictionary<(string, string), ChatActivity> held = [];
    private readonly Dictionary<string, IDisposable> watched = [];

    public ChatActivityTracker(IPluginUIContext context)
    {
        this.context = context;
        context.OnWorkspaceRemoved(workspace =>
        {
            if (watched.Remove(workspace, out var subscription)) subscription.Dispose();
            foreach (var key in held.Keys.Where(key => key.Item1 == workspace).ToArray()) held.Remove(key);
        });
    }

    public event Action<string, string>? Changed;

    public ChatActivity Of(string workspaceId, string chatId) => held.GetValueOrDefault((workspaceId, chatId));

    /// <summary>The delivered snapshot and every later push of a workspace's chats; one subscription per workspace.</summary>
    public void Watch(string workspaceId)
    {
        if (watched.ContainsKey(workspaceId)) return;
        watched[workspaceId] = context.Subscribe(ChatContract.Activity, Apply, new ChatActivityParams(workspaceId));
    }

    internal void Apply(ChatActivityPush push)
    {
        if (held.GetValueOrDefault((push.WorkspaceId, push.ChatId)) == push.Activity) return;
        if (push.Activity == ChatActivity.Idle) held.Remove((push.WorkspaceId, push.ChatId));
        else held[(push.WorkspaceId, push.ChatId)] = push.Activity;
        Changed?.Invoke(push.WorkspaceId, push.ChatId);
    }

    /// <summary>The tab decoration of a chat document of this scheme: the activity mark, which hides itself while the chat is quiet. Null for any other tab.</summary>
    public TabDecoration? Decorate(TabRef tab, string scheme)
    {
        if (tab is not PluginDocumentTabRef document || document.Scheme != scheme) return null;
        Watch(document.WorkspaceId);
        return new TabDecoration { Adornment = () => new ChatActivityMark(this, document.WorkspaceId, document.Id) };
    }

    public void Dispose()
    {
        foreach (var subscription in watched.Values) subscription.Dispose();
        watched.Clear();
    }
}

/// <summary>
/// The mark a chat's tab wears beside its name, the same place, size and look as an agent terminal's: a turning spinner while a turn
/// runs, a warning dot while the chat waits for the user, nothing otherwise. It watches its source, so a tab is never rebuilt for it.
/// </summary>
internal sealed class ChatActivityMark : ContentControl
{
    private readonly IChatActivitySource source;
    private readonly string workspaceId;
    private readonly string chatId;
    private ChatActivity? shown;

    public ChatActivityMark(IChatActivitySource source, string workspaceId, string chatId)
    {
        this.source = source; this.workspaceId = workspaceId; this.chatId = chatId;
        Name = "ChatActivityMark";
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(4, 0, 0, 0);
        AttachedToVisualTree += (_, _) => { source.Changed += Changed; Update(); };
        DetachedFromVisualTree += (_, _) => source.Changed -= Changed;
        Update();
    }

    private void Changed(string workspace, string chat)
    {
        if (workspace == workspaceId && chat == chatId) Update();
    }

    private void Update()
    {
        var activity = source.Of(workspaceId, chatId);
        if (shown == activity) return;
        shown = activity;
        Tag = activity;
        IsVisible = activity != ChatActivity.Idle;
        Content = activity switch
        {
            ChatActivity.Working => new ChatSpinner(),
            ChatActivity.Attention => Dot(Ui.Warning, "Needs your input"),
            _ => null
        };
    }

    private static Ellipse Dot(IBrush brush, string label)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = brush };
        ToolTip.SetTip(dot, label);
        AutomationProperties.SetName(dot, label);
        return dot;
    }
}