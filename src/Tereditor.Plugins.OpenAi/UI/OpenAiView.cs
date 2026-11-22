using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.OpenAi.UI;

/// <summary>
/// The OpenAI component: the shared key row (the key is stored through Model providers, never shown again) and the sign-in policy with a Sign in
/// action that runs Codex's own login in a terminal. tereditor starts no sign-in flow and reads no token.
/// </summary>
internal sealed class OpenAiView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly IPluginDependencyHandle providers;
    private readonly ApiKeyRow keyRow;
    private int generation;
    private readonly TextBlock signInState = Ui.Text("", Ui.Muted, 12);
    private readonly StackPanel signInActions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private ProviderSignIn? signedIn;
    private bool waiting;
    private bool reading;
    private DateTime lastRefresh = DateTime.MinValue;
    private DateTime pollEnd;
    private DispatcherTimer? poll;
    private Window? hostWindow;

    /// <summary>Least time between automatic reads on window activation.</summary>
    internal static TimeSpan Throttle { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Time between reads while a sign-in is awaited.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>How long a sign-in is awaited before polling stops.</summary>
    internal static TimeSpan PollLimit { get; set; } = TimeSpan.FromMinutes(3);

    public OpenAiView(IPluginUIContext context)
    {
        this.context = context;
        providers = context.Dependency(ProvidersContract.Contract);
        Name = "ModelProvider-" + OpenAiProvider.ProviderId;
        Spacing = 4;
        keyRow = new ApiKeyRow("OpenAI",
            secret => Run(() => providers.RequestAsync(ProvidersContract.SaveModelKey, new SaveModelKeyParams(OpenAiProvider.ProviderId, secret)).AsTask()),
            () => Run(() => providers.RequestAsync(ProvidersContract.RemoveModelKey, new ModelProviderParams(OpenAiProvider.ProviderId)).AsTask()));
        Children.Add(keyRow);
        if (OpenAiProvider.Policy.Allowed)
        {
            var signInLine = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(signInActions, Dock.Right);
            signInLine.Children.Add(signInActions);
            signInLine.Children.Add(Ui.Text("Sign in with ChatGPT", Ui.TextBrush, 13));
            Children.Add(signInLine);
            signInState.Name = "OpenAiSignInState";
            signInState.TextWrapping = TextWrapping.Wrap;
            Children.Add(signInState);
        }
        var policy = Ui.Text(OpenAiProvider.SignInText, Ui.Muted, 12);
        policy.Name = "OpenAiSignInPolicy";
        policy.TextWrapping = TextWrapping.Wrap;
        Children.Add(policy);
        Show(null);
        ShowSignIn();
        AttachedToVisualTree += (_, _) =>
        {
            hostWindow = TopLevel.GetTopLevel(this) as Window;
            if (hostWindow is not null) hostWindow.Activated += Activated;
            // Visibility is settled once attaching is done.
            Dispatcher.UIThread.Post(() => Refresh(true), DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            StopPolling();
            if (hostWindow is not null) hostWindow.Activated -= Activated;
            hostWindow = null;
        };
        // The pane lives hidden until its provider is selected; opening it reads the sign-in.
        PropertyChanged += (_, change) =>
        {
            if (change.Property.Name == nameof(IsEffectivelyVisible) && IsEffectivelyVisible) Refresh(true);
        };
    }

    private void Activated(object? sender, EventArgs e) => OnActivated();

    /// <summary>Window activation: a throttled read of the sign-in status.</summary>
    internal void OnActivated() => Refresh(false);

    // Reads the key state and the sign-in status together, on the host (a process there, never on this thread). Without force it runs at most once per Throttle.
    private void Refresh(bool force)
    {
        if (!IsEffectivelyVisible || (!force && DateTime.UtcNow - lastRefresh < Throttle)) return;
        lastRefresh = DateTime.UtcNow;
        ReadSignIn();
    }

    // One read at a time: a read slower than the poll must not be superseded by the next, or its answer would never be shown.
    private void ReadSignIn()
    {
        if (reading) return;
        reading = true;
        Run(async () =>
        {
            try { return await providers.RequestAsync(ProvidersContract.RefreshSignIn, new ModelProviderParams(OpenAiProvider.ProviderId)); }
            finally { reading = false; }
        });
    }

    private void StartPolling()
    {
        StopPolling();
        waiting = true;
        pollEnd = DateTime.UtcNow + PollLimit;
        poll = new DispatcherTimer { Interval = PollInterval };
        poll.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= pollEnd) { StopPolling(); ShowSignIn(); return; }
            ReadSignIn();
        };
        poll.Start();
        ShowSignIn();
    }

    private void StopPolling()
    {
        poll?.Stop();
        poll = null;
        waiting = false;
    }

    // Signed in: the status and Sign out. Waiting: the wait and Cancel. Otherwise: Sign in.
    private void ShowSignIn()
    {
        signInActions.Children.Clear();
        if (signedIn is { SignedIn: true } now)
        {
            signInState.Text = "Signed in" + (now.Method is { Length: > 0 } method ? " with " + method : "") + ". Codex uses this sign-in.";
            var signOut = Ui.Button("Sign out", () => Terminal(OpenAiProvider.SignOutCommand));
            signOut.Name = "OpenAiSignOut";
            ToolTip.SetTip(signOut, "Opens a terminal and runs: " + OpenAiProvider.SignOutCommand);
            signInActions.Children.Add(signOut);
        }
        else if (waiting)
        {
            signInState.Text = "Waiting for sign-in in the browser…";
            var cancel = Ui.Button("Cancel", () => { StopPolling(); ShowSignIn(); });
            cancel.Name = "OpenAiSignInCancel";
            signInActions.Children.Add(cancel);
        }
        else
        {
            signInState.Text = "Not signed in.";
            var signIn = Ui.Button("Sign in", SignIn);
            signIn.Name = "OpenAiSignIn";
            ToolTip.SetTip(signIn, "Opens a terminal and runs: " + OpenAiProvider.SignInCommand);
            signInActions.Children.Add(signIn);
        }
    }

    private void SignIn() => Terminal(OpenAiProvider.SignInCommand, StartPolling);

    private void Terminal(string command, Action? opened = null)
    {
        if (context.Host().ActiveWorkspaceId is not { } workspace)
        {
            context.Notify(PluginNotificationKind.Info, "Open a workspace first", "The sign-in runs in a terminal tab of a workspace.");
            return;
        }
        _ = Open(workspace);
        opened?.Invoke();
        async Task Open(string id)
        {
            try { await context.OpenTerminalAsync(id, new TerminalOpenOptions { Command = command }); }
            catch (Exception error) when (error is not OperationCanceledException) { context.Notify(PluginNotificationKind.Error, "Couldn't open the terminal", error.Message); }
        }
    }

    private void Run(Func<Task<ModelProvidersReport>> request)
    {
        var mine = ++generation;
        _ = Go();
        async Task Go()
        {
            try
            {
                var report = await request();
                if (mine == generation) Show(report);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (mine == generation) context.Notify(PluginNotificationKind.Error, "Couldn't update the OpenAI settings", error.Message);
            }
        }
    }

    private void Show(ModelProvidersReport? report)
    {
        signedIn = report?.Providers.FirstOrDefault(row => row.Provider.Id == OpenAiProvider.ProviderId)?.SignIn;
        if (signedIn is { SignedIn: true }) StopPolling();
        ShowSignIn();
        var stored = report is not null && OpenAiProvider.KeyStored(report);
        keyRow.Hints.Clear();
        keyRow.Hints.Add(stored ? "API key stored. Codex can use OpenAI as its model source." : "No API key yet. Add one to let Codex use OpenAI as its model source.");
        keyRow.Show("OpenAI API" + (stored ? "  (ready)" : ""), stored);
    }
}