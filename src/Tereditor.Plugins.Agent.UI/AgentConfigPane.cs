using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Agent.UI;

/// <summary>The compact "Early support" tag.</summary>
public sealed partial class EarlySupportBadge : Border
{
    /// <summary>Creates the tag.</summary>
    public EarlySupportBadge() => AvaloniaXamlLoader.Load(this);
}

internal sealed partial class AgentPaneHeader : DockPanel
{
    internal AgentPaneHeader() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// An agent's content in the Agent pane: the first row (title, badge and reload button), the move to another workspace and the watching
/// that lasts while the pane is on screen. The agent draws its tabs below the first row.
/// </summary>
public abstract class AgentConfigPane : DockPanel
{
    private IDisposable? watch;
    private int generation;

    /// <summary>Creates the pane. <paramref name="prefix"/> starts the first row's control names; <paramref name="agent"/> names the agent in words.</summary>
    protected AgentConfigPane(string workspaceId, string prefix, string agent, string? title = null, string? badgeTip = null)
    {
        WorkspaceId = workspaceId;
        Background = Ui.Sidebar;
        var header = new AgentPaneHeader();
        var heading = header.FindControl<StackPanel>("Heading")!;
        var badge = header.FindControl<EarlySupportBadge>("Badge")!;
        var refresh = header.FindControl<Button>("Refresh")!;
        heading.Name = prefix + "Heading";
        if (title is not null) heading.Children.Insert(0, Ui.Text(title, Ui.TextBrush, 13));
        badge.Name = prefix + "EarlyBadge";
        ToolTip.SetTip(badge, badgeTip);
        refresh.Name = prefix + "Refresh";
        refresh.Content = Ui.Icon("refresh");
        ToolTip.SetTip(refresh, "Re-read " + agent);
        AutomationProperties.SetName(refresh, "Re-read " + agent);
        refresh.Click += (_, _) => Reload();
        SetDock(header, Dock.Top);
        Children.Add(header);
        AttachedToVisualTree += (_, _) => { watch ??= Watch(); Reload(); };
        DetachedFromVisualTree += (_, _) => { watch?.Dispose(); watch = null; };
    }

    /// <summary>The workspace the pane describes now.</summary>
    protected string WorkspaceId { get; private set; }

    /// <summary>Starts a read; replies of earlier reads, and of another workspace, are no longer <see cref="IsCurrent"/>.</summary>
    protected int NextRead() => ++generation;

    /// <summary>Whether a read's reply may still be shown. UI thread.</summary>
    protected bool IsCurrent(int read) => read == generation;

    /// <summary>Points the pane at another workspace: the old rows and any reply still in flight for them are dropped.</summary>
    public bool Retarget(string workspaceId)
    {
        if (workspaceId == WorkspaceId) return true;
        WorkspaceId = workspaceId;
        generation++;
        Forget();
        if (TopLevel.GetTopLevel(this) is not null) Reload();
        return true;
    }

    /// <summary>Shows one of the four tabs the Agent pane offers.</summary>
    public abstract void ShowTab(AgentPaneTab tab);

    /// <summary>Starts watching for changes; disposed when the pane leaves the screen.</summary>
    protected abstract IDisposable Watch();

    /// <summary>Reads again: on show, on the reload button and after a move.</summary>
    protected abstract void Reload();

    /// <summary>Drops what was drawn for the workspace the pane just left.</summary>
    protected abstract void Forget();

    /// <summary>Opens a file of the shown workspace, and says so when that fails.</summary>
    protected async void OpenFile(IPluginUIContext context, string path)
    {
        try
        {
            if (await context.Editors.OpenAsync(WorkspaceId, path) is null) throw new IOException();
        }
        catch (Exception) { context.Notify(PluginNotificationKind.Error, $"Couldn't open {ScopedSetting.AbbreviateHomePath(path)}"); }
    }

    /// <summary>The pane's registration. It owns the terminals that run the agent and the documents of <paramref name="documentScheme"/>.</summary>
    public static AgentPaneRegistration Registration(IPluginUIContext context, string agentId, string label, string icon, Func<string, AgentConfigPane> create,
        Func<bool> usable, string? documentScheme = null) => new(agentId, label, icon, create)
        {
            Usable = usable,
            Owns = tab => tab is PluginDocumentTabRef document ? document.Scheme == documentScheme : Runs(context, tab, agentId),
            Retarget = (pane, workspace) => pane is AgentConfigPane own && own.Retarget(workspace),
            ShowTab = (pane, tab) => (pane as AgentConfigPane)?.ShowTab(tab)
        };

    /// <summary>Whether the tab is a terminal the host sees <paramref name="agentId"/> running in. Reads in-memory state only.</summary>
    public static bool Runs(IPluginUIContext context, TabRef tab, string agentId) =>
        tab is TerminalTabRef terminal && context.Host().Terminals.GetValueOrDefault(terminal.WorkspaceId)?
            .FirstOrDefault(item => item.TabKey == terminal.TabKey)?.Agent?.Kind == agentId;

    /// <summary>The icon button that starts an agent from the strip of a workspace's tabs; the caller handles its click.</summary>
    public static Button StartButton(string name, string accessibleName, string tooltip, Control glyph)
    {
        var button = new Button
        {
            Name = name,
            Content = glyph,
            Width = 32,
            Height = 32,
            Padding = new Thickness(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        Ui.AsIconButton(button);
        AutomationProperties.SetName(button, accessibleName);
        ToolTip.SetTip(button, tooltip);
        return button;
    }
}