using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.AirGateway.UI;

/// <summary>
/// The Central pane of JetBrains (Air Gateway) in Settings › Plugins › Model providers. Which agents Central governs is each agent's Model source, not a setting here. Only the local, read-only
/// sign-in status check runs automatically (on open, on window activation at most every <see cref="Throttle"/>,
/// after sign-in and sign-out, and every <see cref="PollInterval"/> for at most <see cref="PollLimit"/> while a
/// sign-in is awaited, only while the pane is attached and visible); every other action is a button. The AI access
/// sources are listed automatically (on the host, never on the UI thread) while Central governs an agent and is
/// signed in: on open, when an agent starts using Central, after sign-in, after a switch and on the throttled
/// activation check./// </summary>
internal sealed partial class AirSettingsView : UserControl
{
    private readonly IPluginUIContext context;
    private readonly TextBlock centralLine, credits, launchNote, result, accessNote;
    private readonly StackPanel install, access;
    private readonly TextBox installCommand;
    private readonly Button signIn, signOut, startProxy, update;
    private readonly List<IDisposable> observers = [];
    private AirStatus? status;
    private bool busy;
    private bool checking;
    private long generation;
    private long listGeneration;
    private DateTime lastList = DateTime.MinValue;
    private string? wasAuth;
    private DateTime lastCheck = DateTime.MinValue;
    private DispatcherTimer? poll;
    private DateTime pollEnd;
    private Window? hostWindow;

    /// <summary>Least time between automatic checks on window activation.</summary>
    internal static TimeSpan Throttle { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Time between checks while a sign-in is awaited.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>How long a sign-in is awaited before polling stops.</summary>
    internal static TimeSpan PollLimit { get; set; } = TimeSpan.FromMinutes(3);

    public AirSettingsView(IPluginUIContext context)
    {
        this.context = context;
        AvaloniaXamlLoader.Load(this);
        T Get<T>(string name) where T : Control => this.FindControl<T>(name)!;
        centralLine = Get<TextBlock>("AirCentralLine"); credits = Get<TextBlock>("AirCredits"); launchNote = Get<TextBlock>("AirLaunchNote");
        result = Get<TextBlock>("AirResult"); accessNote = Get<TextBlock>("AirAccessNote");
        install = Get<StackPanel>("AirInstall"); access = Get<StackPanel>("AirAccessSources");
        installCommand = Get<TextBox>("AirInstallCommand");
        signIn = Get<Button>("AirSignIn"); signOut = Get<Button>("AirSignOut");
        startProxy = Get<Button>("AirStartProxy"); update = Get<Button>("AirUpdate");
        signIn.Click += (_, _) => _ = RunAsync(AirGatewayContract.SignIn);
        signOut.Click += (_, _) => _ = RunAsync(AirGatewayContract.SignOut);
        startProxy.Click += (_, _) => _ = RunAsync(AirGatewayContract.StartProxy);
        update.Click += (_, _) => _ = RunAsync(AirGatewayContract.Update);
        Render();
        AttachedToVisualTree += (_, _) =>
        {
            if (observers.Count > 0) return;
            generation++;
            hostWindow = TopLevel.GetTopLevel(this) as Window;
            if (hostWindow is not null) hostWindow.Activated += Activated;
            // The host pushes a status when an agent's Model source changes; a change in who is governed refreshes the credits and the listing.
            observers.Add(context.Subscribe(AirGatewayContract.Changed, next =>
            {
                var was = Governed;
                status = next;
                Render();
                if (was != Governed) _ = RefreshAsync(true, true);
            }, new AirStatusParams()));
            // After the observers: a check with none registered is skipped, so the on-open check must follow them.
            _ = CheckAsync(true);
        };
        // The pane lives hidden in the provider's pane until its row is selected; opening it checks the sign-in.
        PropertyChanged += (_, change) =>
        {
            if (change.Property.Name == nameof(IsEffectivelyVisible) && IsEffectivelyVisible) _ = CheckAsync(true);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            generation++;
            StopPolling();
            if (hostWindow is not null) hostWindow.Activated -= Activated;
            hostWindow = null;
            checking = false;
            foreach (var observer in observers) observer.Dispose();
            observers.Clear();
        };
    }

    /// <summary>Whether an agent's Model source is JetBrains (Air Gateway), as the host last said.</summary>
    private bool Governed => status?.Agents.Any(agent => agent.Chosen) == true;

    private void Activated(object? sender, EventArgs e) => OnActivated();

    /// <summary>Window activation: a throttled automatic check.</summary>
    internal void OnActivated() => _ = CheckAsync(false);

    /// <summary>
    /// The automatic, read-only sign-in check. Never overlaps another, is dropped when the pane detached meanwhile,
    /// and without <paramref name="force"/> runs at most once per <see cref="Throttle"/>.
    /// </summary>
    private async Task CheckAsync(bool force)
    {
        if (checking || observers.Count == 0 || !IsEffectivelyVisible || (!force && DateTime.UtcNow - lastCheck < Throttle)) return;
        checking = true; lastCheck = DateTime.UtcNow;
        var mine = generation;
        Render();
        try
        {
            var next = await context.RequestAsync(AirGatewayContract.Status, new AirStatusParams(true));
            if (mine != generation) return;
            status = next;
            if (next.Central.Auth == "connected") StopPolling();
            var signedInNow = next.Central.Auth == "connected" && wasAuth != "connected";
            wasAuth = next.Central.Auth;
            _ = ListAccessAsync(force || signedInNow);
        }
        catch (PluginCallException error) { if (mine == generation) result.Text = error.Message; }
        finally { if (mine == generation) { checking = false; lastCheck = DateTime.UtcNow; Render(); } }
    }

    private void StartPolling()
    {
        StopPolling();
        pollEnd = DateTime.UtcNow + PollLimit;
        poll = new DispatcherTimer { Interval = PollInterval };
        poll.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= pollEnd || observers.Count == 0) { StopPolling(); return; }
            _ = CheckAsync(true);
        };
        poll.Start();
    }

    private void StopPolling() { poll?.Stop(); poll = null; }

    private async Task RefreshAsync(bool probe, bool sourceChanged = false)
    {
        try
        {
            status = await context.RequestAsync(AirGatewayContract.Status, new AirStatusParams(probe));
            wasAuth = status.Central.Auth;
            Render();
        }
        catch (PluginCallException error) { result.Text = error.Message; return; }
        _ = ListAccessAsync(sourceChanged);
    }

    private async Task RunAsync(PluginMethod<AirActionParams, AirOutcome> method)
    {
        busy = true; Render();
        var mine = generation;
        var automatic = method == AirGatewayContract.SignIn || method == AirGatewayContract.SignOut;
        if (method == AirGatewayContract.SignOut) StopPolling();
        try
        {
            var outcome = await context.RequestAsync(method, new AirActionParams());
            if (mine != generation) return;
            status = outcome.Status;
            result.Text = outcome.Message;
        }
        catch (PluginCallException error) { if (mine == generation) result.Text = error.Message; automatic = false; }
        finally { if (mine == generation) { busy = false; Render(); } }
        if (!automatic || mine != generation) return;
        await CheckAsync(true);
        if (method == AirGatewayContract.SignIn && status?.Central.Auth != "connected" && status?.Central.State is "supported" or "outdated") StartPolling();
    }

    /// <summary>
    /// Lists the AI access sources when Central is the chosen source, supported and signed in; otherwise shows why not.
    /// Without <paramref name="force"/> it runs at most once per <see cref="Throttle"/>. A newer request, the pane
    /// detaching or a changed source drops an older result.
    /// </summary>
    private async Task ListAccessAsync(bool force)
    {
        var mine = generation;
        if (observers.Count == 0) return;
        if (!Governed) { ShowAccess(null, ""); return; }
        var central = status?.Central;
        if (central is null) { ShowAccess(null, "Checking Central…"); return; }
        if (central.State != "supported") { ShowAccess(null, central.State == "absent" ? "Central is not installed, so its access sources can't be listed." : "Central is not available, so its access sources can't be listed."); return; }
        if (central.Auth == "signed-out") { ShowAccess(null, "Sign in to Central to see its AI access sources."); return; }
        if (central.Auth != "connected") { ShowAccess(null, "Checking Central's sign-in…"); return; }
        if (!force && DateTime.UtcNow - lastList < Throttle) return;
        lastList = DateTime.UtcNow;
        var token = ++listGeneration;
        accessNote.Text = "Loading access sources…";
        AirAccessList list;
        try { list = await context.RequestAsync(AirGatewayContract.AccessSources, new AirActionParams()); }
        catch (PluginCallException error)
        {
            if (mine == generation && token == listGeneration) ShowAccess(null, error.Message);
            return;
        }
        if (mine != generation || token != listGeneration || !Governed) return;
        ShowAccess(list.Sources, list.Detail ?? "");
    }

    private void ShowAccess(IReadOnlyList<AirAccessSource>? sources, string note)
    {
        listGeneration++;
        access.Children.Clear();
        accessNote.Text = note;
        foreach (var source in sources ?? [])
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            row.Children.Add(Ui.Text(source.Name + (source.Current ? " (current)" : ""), Ui.TextBrush));
            if (!source.Current)
            {
                var button = new Button { Name = "AirSwitchAccess", Tag = source.Id, Content = "Switch" };
                button.Click += (_, _) => _ = SwitchAsync(source.Id);
                Grid.SetColumn(button, 1);
                row.Children.Add(button);
            }
            access.Children.Add(row);
        }
    }

    private async Task SwitchAsync(string id)
    {
        busy = true; Render();
        var mine = generation;
        try
        {
            var outcome = await context.RequestAsync(AirGatewayContract.SwitchAccess, new AirSwitchParams(id));
            if (mine != generation) return;
            status = outcome.Status;
            result.Text = outcome.Message;
        }
        catch (PluginCallException error) { if (mine == generation) result.Text = error.Message; }
        finally { if (mine == generation) { busy = false; Render(); } }
        if (mine == generation) await ListAccessAsync(true);
    }

    internal static string CentralLine(AirCentral central, bool checking = false) => central.State switch
    {
        "absent" => "Central is not installed on this host.",
        "outdated" => $"Central {central.Version} is older than the minimum this plugin supports.",
        "malformed-version" => "This Central's version could not be verified. Reinstall it, then recheck.",
        "probe-failed" => "Central could not be verified right now. Check its installation, then recheck.",
        _ => $"Central {central.Version} at {central.Path}" + (checking ? " · checking sign-in…" : central.Auth) switch
        {
            " · checking sign-in…" => " · checking sign-in…",
            "connected" => " · signed in",
            "signed-out" => " · signed out",
            _ => " · sign-in not checked"
        } + (central.Proxy == "stopped" ? " · proxy stopped" : "")
    };

    /// <summary>The credits line, or empty when they are unknown; nothing is shown rather than a guess.</summary>
    internal static string CreditsLine(AirStatus status)
    {
        if (status.Credits is not { } credits) return "";
        var text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{credits.Remaining:0.00} of {credits.Total:0.00} credits remaining");
        if (credits.RefillNext is { } millis && millis > 0 && millis < 253_402_300_799_000)
            text += ", refills " + DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (credits.TopUp > 0) text += string.Create(System.Globalization.CultureInfo.InvariantCulture, $" (plus {credits.TopUp:0.00} top-up credits)");
        return text;
    }

    private void Render()
    {
        var current = status;
        var working = busy || current?.Busy == true;
        if (current is null) { Toggle(working); return; }
        centralLine.Text = CentralLine(current.Central, checking);
        credits.Text = CreditsLine(current);
        credits.IsVisible = credits.Text.Length > 0;
        install.IsVisible = current.Central.State == "absent";
        installCommand.Text = current.Central.InstallCommand;
        startProxy.IsVisible = current.Central.Proxy == "stopped";
        update.IsVisible = current.Central.State == "outdated";
        launchNote.Text = !Governed ? "" :
            current.Central.State is "absent" or "malformed-version" or "probe-failed" ? "Central was not found or could not be verified: governed launches will fail in the terminal rather than start without governance." :
            current.Central.State == "outdated" ? "This Central is older than the minimum supported; update it before launching." :
            "An agent whose command is customized in its own settings keeps that command and is not governed.";
        Toggle(working);
    }

    private void Toggle(bool working)
    {
        var idle = !working;
        var central = status?.Central;
        signIn.IsEnabled = idle && central is { State: "supported" or "outdated" } && central.Auth != "connected";
        signOut.IsEnabled = idle && central is { State: "supported" or "outdated" } && central.Auth != "signed-out";
        startProxy.IsEnabled = update.IsEnabled = idle;
    }
}