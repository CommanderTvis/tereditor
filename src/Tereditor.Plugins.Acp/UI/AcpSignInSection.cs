using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Acp.UI;

/// <summary>
/// "Signs in with" in an agent's settings: the sources the host found, in the order the agent checks them, with the reason in a tooltip,
/// and a button that runs the agent's own terminal login in a terminal tab. The host does the detection; nothing here touches a process or a file.
/// </summary>
internal sealed class AcpSignInSection : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly string agentId;
    private int generation;

    internal AcpSignInSection(IPluginUIContext context, string agentId)
    {
        this.context = context;
        this.agentId = agentId;
        Name = "AcpSignIn_" + agentId;
        Spacing = 4;
        Refresh();
    }

    /// <summary>Asks the host again, for example after the user signed in in the terminal.</summary>
    internal void Refresh()
    {
        var mine = ++generation;
        Children.Clear();
        Children.Add(Line("Signs in with: checking…", muted: true));
        _ = LoadAsync(mine);
    }

    private async Task LoadAsync(int mine)
    {
        AcpSignInInfo info;
        try { info = await Task.Run(() => context.RequestAsync(AcpContract.SignIn, new AcpSignInParams(agentId)).AsTask()); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (mine == generation) Show(null, error.Message); });
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() => { if (mine == generation) Show(info, null); });
    }

    private static TextBlock Line(string text, bool muted = false) =>
        new() { Text = text, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = muted ? Ui.Muted : Ui.TextBrush };

    private void Show(AcpSignInInfo? info, string? failure)
    {
        Children.Clear();
        var heading = Line("Signs in with", muted: true);
        heading.Name = "AcpSignInHeading";
        Children.Add(heading);
        if (info is null)
        {
            Children.Add(Line("Could not check: " + failure));
            return;
        }
        ToolTip.SetTip(heading, info.Why);
        var index = 0;
        foreach (var line in info.Lines)
        {
            var row = Line("• " + line.Display());
            row.Name = "AcpSignInLine_" + index++;
            ToolTip.SetTip(row, info.Why);
            Children.Add(row);
        }
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        if (info.Terminal is { } terminal)
        {
            var run = Ui.Button(terminal.Label is { Length: > 0 } label ? "Run in terminal: " + label : "Run in terminal", () => _ = RunAsync(terminal.CommandLine!));
            run.Name = "AcpSignInRun";
            ToolTip.SetTip(run, "Opens a terminal and runs: " + terminal.CommandLine);
            actions.Children.Add(run);
        }
        var again = Ui.Button("Check again", Refresh);
        again.Name = "AcpSignInAgain";
        actions.Children.Add(again);
        Children.Add(actions);
    }

    private async Task RunAsync(string commandLine)
    {
        if (context.Host().ActiveWorkspaceId is not { } workspace)
        {
            context.Notify(PluginNotificationKind.Info, "Open a workspace first", "The sign-in runs in a terminal tab of a workspace.");
            return;
        }
        try
        {
            await context.OpenTerminalAsync(workspace, new TerminalOpenOptions { Command = commandLine });
            // The user signs in there; the next time this page gets focus it asks the host again.
            recheck = true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't open the terminal", error.Message);
        }
    }

    private TopLevel? top;
    private bool recheck;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        top = TopLevel.GetTopLevel(this);
        if (top is not null) top.GotFocus += Focused;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (top is not null) top.GotFocus -= Focused;
        top = null;
        base.OnDetachedFromVisualTree(e);
    }

    // After the user used the terminal, the next time focus comes back to the window the host is asked again.
    private void Focused(object? sender, EventArgs e)
    {
        if (!recheck) return;
        recheck = false;
        Refresh();
    }
}
