using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Agent.UI;

internal sealed partial class AgentSettingsStyles : Styles
{
    public AgentSettingsStyles() => AvaloniaXamlLoader.Load(this);
}

/// <summary>Where an agent comes from: the source cards, the pinned download with its buttons, and the system command.</summary>
public sealed partial class AgentSourceSection : StackPanel
{
    /// <summary>Creates the section; <see cref="AgentSettingsPage{TSettings, TParams, TStatus}"/> drives it.</summary>
    public AgentSourceSection() => AvaloniaXamlLoader.Load(this);
}

/// <summary>What the host says about an agent's pinned download.</summary>
public sealed record AgentDownload(bool Installing, string Progress, string Error, string PinnedVersion, string ManagedVersion);

/// <summary>
/// The settings page of an agent that runs from a system command or from a pinned copy tereditor downloads. The page's XAML holds an
/// <see cref="AgentSourceSection"/> named <c>SourceSection</c>. This class draws it, saves settings and asks the host for the download's
/// state, each half second while one runs. It asks for a download only on a click on Download.
/// </summary>
public abstract class AgentSettingsPage<TSettings, TParams, TStatus> : StackPanel
    where TSettings : class where TParams : new() where TStatus : class
{
    private const string Managed = "managed";

    private readonly IPluginUIContext context;
    private readonly string agent;
    private readonly Func<TStatus?> status;
    private readonly Action<TStatus> update;
    private readonly PluginMethod<TParams, TStatus> read;
    private Action<TSettings> redraw = _ => { };
    private DownloadStrip? strip;
    private bool applying, polling, attached;
    private string readFor = "";
    private IDisposable? subscription;

    /// <summary>Creates the page. <paramref name="status"/> and <paramref name="update"/> are the plugin's last read of the state.</summary>
    protected AgentSettingsPage(IPluginUIContext context, string agent, PluginMethod<TParams, TStatus> read, Func<TStatus?> status, Action<TStatus> update)
    {
        this.context = context;
        this.agent = agent;
        this.read = read;
        this.status = status;
        this.update = update;
        Styles.Add(new AgentSettingsStyles());
    }

    /// <summary>The plugin's last read of the state.</summary>
    protected TStatus? Status => status();

    /// <summary>What the state says about the download.</summary>
    protected abstract AgentDownload Download(TStatus state);

    /// <summary>The state to show right after a click on Download, before the host answers.</summary>
    protected abstract TStatus Starting(TStatus? state, string progress);

    /// <summary>The source the page shows as chosen, <c>managed</c> or <c>system</c>.</summary>
    protected abstract string ShownSource(TSettings settings);

    /// <summary>The saved source (a change reads the state again) and the saved command.</summary>
    protected abstract (string Source, string Command) Saved(TSettings settings);

    /// <summary>The settings with another source or command.</summary>
    protected abstract TSettings With(TSettings settings, string? source = null, string? command = null);

    /// <summary>A warning about the source in use, under the cards; empty shows none.</summary>
    protected virtual string SourceNote => "";

    /// <summary>Shows the settings and the state in the agent's own controls. A change it makes saves nothing.</summary>
    protected abstract void Render(TSettings settings);

    /// <summary>Wires the source section and shows the page. The agent's constructor calls it last.</summary>
    protected void Start(IReadOnlyList<OptionCard> sources, string commandTitle, string commandName,
        PluginMethod<TParams, TStatus> install, PluginMethod<TParams, TStatus> cancel, PluginMethod<TParams, TStatus> remove)
    {
        var section = this.FindControl<AgentSourceSection>("SourceSection")!;
        T Part<T>(string name) where T : Control => section.FindControl<T>(name)!;
        var cards = Part<OptionCards>("Source");
        var note = Part<TextBlock>("SourceNote");
        var managed = Part<StackPanel>("ManagedPanel");
        var line = Part<TextBlock>("ManagedLine");
        var error = Part<TextBlock>("ManagedError");
        var progress = Part<StackPanel>("ManagedProgress");
        var download = Part<Button>("ManagedDownload");
        var delete = Part<Button>("ManagedRemove");
        var commandPanel = Part<StackPanel>("CommandPanel");
        var command = Part<TextBox>("CommandInput");
        var word = agent.ToLowerInvariant();
        Part<TextBlock>("SourceTitle").Text = agent + " source";
        Part<TextBlock>("CommandTitle").Text = commandTitle;
        command.PlaceholderText = word;
        AutomationProperties.SetName(cards, agent + " source");
        AutomationProperties.SetName(command, commandName);
        AutomationProperties.SetName(download, "Download pinned " + agent);
        AutomationProperties.SetName(delete, "Remove downloaded " + agent);
        cards.SetOptions(sources, null);
        redraw = settings =>
        {
            applying = true;
            var shown = ShownSource(settings);
            cards.Show(shown);
            note.Text = SourceNote;
            note.IsVisible = SourceNote.Length > 0;
            managed.IsVisible = shown == Managed;
            commandPanel.IsVisible = shown != Managed;
            var state = status() is { } current ? Download(current) : null;
            var installing = state is { Installing: true };
            var found = state?.ManagedVersion ?? "";
            var pinned = state?.PinnedVersion ?? "";
            var latest = found.Length > 0 && found == pinned;
            line.Text = installing ? state!.Progress
                : latest ? $"{agent} {found} is downloaded. It is the pinned version."
                : found.Length > 0 ? $"{agent} {found} is downloaded, but {agent} {pinned} is pinned. Update to use it."
                : pinned.Length > 0 ? $"{agent} {pinned} is pinned and not downloaded yet. Press Download to get it."
                : "";
            line.IsVisible = !installing;
            error.Text = state?.Error ?? "";
            error.IsVisible = error.Text.Length > 0;
            if (installing && strip is null) progress.Children.Add(strip = new DownloadStrip(state!.Progress, () => _ = RequestAsync(cancel)));
            else if (!installing && strip is not null)
            {
                progress.Children.Clear();
                strip = null;
            }
            if (strip is not null) strip.Message = state!.Progress;
            download.Content = found.Length > 0 && !latest ? "Update" : "Download";
            download.IsEnabled = !installing && !latest;
            delete.IsEnabled = !installing && found.Length > 0;
            if (!command.IsFocused) command.Text = Saved(settings).Command;
            Render(settings);
            applying = false;
        };
        BindText(command, () => command.Text, settings => Saved(settings).Command, (settings, value) => With(settings, command: value), word);
        cards.Chosen += chosen =>
        {
            if (chosen != ShownSource(context.Settings<TSettings>())) Save(settings => With(settings, source: chosen));
        };
        download.Click += (_, _) =>
        {
            update(Starting(status(), "Looking for Bun…"));
            redraw(context.Settings<TSettings>());
            _ = RequestAsync(install);
            if (!polling) _ = PollAsync();
        };
        delete.Click += (_, _) => _ = RequestAsync(remove);
        redraw(context.Settings<TSettings>());
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            redraw(context.Settings<TSettings>());
            _ = RequestAsync(read);
            subscription ??= context.OnSettings<TSettings>(settings =>
            {
                redraw(settings);
                if (Saved(settings).Source != readFor) _ = RequestAsync(read);
                readFor = Saved(settings).Source;
            });
        };
        DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            subscription?.Dispose();
            subscription = null;
        };
    }

    /// <summary>Saves a change to the settings, and says so when that fails.</summary>
    protected async void Save(Func<TSettings, TSettings> change)
    {
        try { await context.UpdateSettingsAsync(change(context.Settings<TSettings>())); }
        catch (Exception) { context.Notify(PluginNotificationKind.Error, $"Couldn't change the {agent} setting"); }
    }

    /// <summary>Saves a text or suggestion box when it loses focus, on Enter and when its list closes. A blank value saves as <paramref name="fallback"/>.</summary>
    protected void BindText(Control input, Func<string?> text, Func<TSettings, string> current, Func<TSettings, string, TSettings> write, string fallback = "")
    {
        void Commit()
        {
            var value = text()?.Trim() is { Length: > 0 } typed ? typed : fallback;
            if (!applying && value != current(context.Settings<TSettings>())) Save(settings => write(settings, value));
        }
        input.LostFocus += (_, _) => Commit();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        if (input is AutoCompleteBox box) box.DropDownClosed += (_, _) => Commit();
    }

    // The host does the download; the page asks for its state while one runs, so it also shows a download started elsewhere.
    private async Task RequestAsync(PluginMethod<TParams, TStatus> method)
    {
        try { update(await context.RequestAsync(method, new TParams())); }
        catch (Exception error) when (error is not OperationCanceledException) { }
        redraw(context.Settings<TSettings>());
        if (status() is { } state && Download(state).Installing && !polling) _ = PollAsync();
    }

    private async Task PollAsync()
    {
        polling = true;
        try
        {
            while (attached && status() is { } state && Download(state).Installing)
            {
                await Task.Delay(500);
                await RequestAsync(read);
            }
        }
        finally { polling = false; }
    }
}