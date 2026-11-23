using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Pi.UI;

/// <summary>Settings › Pi: where Pi comes from (the command, or the pinned copy tereditor downloads) and the model and thinking level a new chat starts with.</summary>
internal sealed partial class PiSettingsView : StackPanel
{
    internal PiSettingsView(IPluginUIContext context, Func<PiStatus?> status, Action<PiStatus?> update)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<StackPanel>("ModelSourceHost")!.Children.Add(UiSlots.ModelSourceView(context, PiManifest.Id));
        var applying = false;
        var command = this.FindControl<TextBox>("PiCommandInput")!;
        var model = this.FindControl<TextBox>("PiModel")!;
        var thinking = this.FindControl<TextBox>("PiThinking")!;
        var codemode = this.FindControl<CheckBox>("PiCodemode")!;
        var version = this.FindControl<TextBlock>("PiVersion")!;
        var source = this.FindControl<ComboBox>("PiSource")!;
        var sourceHint = this.FindControl<TextBlock>("PiSourceHint")!;
        var managedPanel = this.FindControl<StackPanel>("PiManagedPanel")!;
        var commandPanel = this.FindControl<StackPanel>("PiCommandPanel")!;
        var managedLine = this.FindControl<TextBlock>("PiManagedLine")!;
        var managedError = this.FindControl<TextBlock>("PiManagedError")!;
        var managedProgress = this.FindControl<StackPanel>("PiManagedProgress")!;
        var download = this.FindControl<Button>("PiManagedDownload")!;
        var remove = this.FindControl<Button>("PiManagedRemove")!;
        DownloadStrip? strip = null;
        var polling = false;
        var attached = false;
        var shownSource = "";

        void Update(Func<PiSettings, PiSettings> change) => _ = UpdateAsync(change);
        async Task UpdateAsync(Func<PiSettings, PiSettings> change)
        {
            try { await context.UpdateSettingsAsync(change(context.Settings<PiSettings>())); }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change the Pi setting"); }
        }
        void Text(TextBox input, Func<PiSettings, string> current, Func<PiSettings, string, PiSettings> write, string fallback = "")
        {
            void Save()
            {
                var value = input.Text?.Trim() is { Length: > 0 } typed ? typed : fallback;
                if (!applying && value != current(context.Settings<PiSettings>())) Update(settings => write(settings, value));
            }
            input.LostFocus += (_, _) => Save();
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); e.Handled = true; } };
        }
        Text(command, settings => settings.Command, (settings, value) => settings with { Command = value }, "pi");
        Text(model, settings => settings.Model, (settings, value) => settings with { Model = value });
        Text(thinking, settings => settings.Thinking, (settings, value) => settings with { Thinking = value });
        codemode.Click += (_, _) => Update(settings => settings with { Codemode = codemode.IsChecked == true });

        // The host does the download; the page asks for its state while one runs, so it also shows a download a chat started.
        async Task RefreshAsync(Func<Task<PiStatus>>? request = null)
        {
            try { update(await (request?.Invoke() ?? context.RequestAsync(PiContract.Status, new PiNoParams()).AsTask())); }
            catch (Exception error) when (error is not OperationCanceledException) { }
            Render(context.Settings<PiSettings>());
            if (status() is { Installing: true } && !polling) _ = PollAsync();
        }
        async Task PollAsync()
        {
            polling = true;
            try
            {
                while (attached && status() is { Installing: true })
                {
                    await Task.Delay(500);
                    await RefreshAsync();
                }
            }
            finally { polling = false; }
        }
        source.SelectionChanged += (_, _) =>
        {
            var chosen = source.SelectedIndex == 1 ? PiSources.Managed : PiSources.System;
            if (!applying && chosen != context.Settings<PiSettings>().Source) Update(settings => settings with { Source = chosen });
        };
        download.Click += (_, _) =>
        {
            update((status() ?? new PiStatus(false, "managed")) with { Installing = true, Progress = "Looking for Bun…", Error = "" });
            Render(context.Settings<PiSettings>());
            _ = RefreshAsync(() => context.RequestAsync(PiContract.Install, new PiNoParams()).AsTask());
            if (!polling) _ = PollAsync();
        };
        remove.Click += (_, _) => _ = RefreshAsync(() => context.RequestAsync(PiContract.Remove, new PiNoParams()).AsTask());

        void Render(PiSettings settings)
        {
            applying = true;
            var managed = settings.Source == PiSources.Managed;
            source.SelectedIndex = managed ? 1 : 0;
            managedPanel.IsVisible = managed;
            commandPanel.IsVisible = !managed;
            sourceHint.Text = managed
                ? "Runs the pinned Pi that tereditor downloads, on Bun from Runtimes for tooling. It never runs the Pi on your PATH."
                : "Runs the command below, found on your PATH by default.";
            RenderManaged(status());
            if (!command.IsFocused) command.Text = settings.Command;
            if (!model.IsFocused) model.Text = settings.Model;
            if (!thinking.IsFocused) thinking.Text = settings.Thinking;
            codemode.IsChecked = settings.Codemode;
            version.Text = status() switch
            {
                { Source: PiSources.Managed, Installed: true, Version: { } pinned } => "Pi v" + pinned + " (pinned)",
                { Source: PiSources.Managed } => "",
                { Installed: true, Version: { } found } => "Pi v" + found,
                { Installed: true } => "Pi is installed.",
                { Detail: { } detail } => "Pi is not installed: " + detail,
                _ => ""
            };
            applying = false;
        }
        void RenderManaged(PiStatus? state)
        {
            var installing = state is { Installing: true };
            var found = state?.ManagedVersion ?? "";
            var pinned = state?.PinnedVersion ?? "";
            managedLine.Text = installing ? state!.Progress
                : found.Length > 0 && found == pinned ? $"Pi {found} is downloaded. It is the pinned version."
                : found.Length > 0 ? $"Pi {found} is downloaded, but Pi {pinned} is pinned. Update to use it."
                : pinned.Length > 0 ? $"Pi {pinned} is pinned and not downloaded yet. A chat downloads it when you start one."
                : "";
            managedError.Text = state?.Error ?? "";
            managedError.IsVisible = managedError.Text.Length > 0;
            if (installing && strip is null)
            {
                strip = new DownloadStrip(state!.Progress, () => _ = RefreshAsync(() => context.RequestAsync(PiContract.CancelInstall, new PiNoParams()).AsTask()));
                managedProgress.Children.Add(strip);
            }
            else if (!installing && strip is not null)
            {
                managedProgress.Children.Clear();
                strip = null;
            }
            if (strip is not null) strip.Message = state!.Progress;
            managedLine.IsVisible = !installing;
            download.Content = found.Length > 0 && found != pinned ? "Update" : "Download";
            download.IsEnabled = !installing && !(found.Length > 0 && found == pinned);
            remove.IsEnabled = !installing && found.Length > 0;
        }
        Render(context.Settings<PiSettings>());
        IDisposable? subscription = null;
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            Render(context.Settings<PiSettings>());
            _ = RefreshAsync();
            subscription ??= context.OnSettings<PiSettings>(settings =>
            {
                Render(settings);
                if (settings.Source != shownSource) _ = RefreshAsync();
                shownSource = settings.Source;
            });
        };
        DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            subscription?.Dispose();
            subscription = null;
        };
    }
}