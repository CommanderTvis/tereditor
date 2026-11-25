using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Omp.UI;

/// <summary>Settings › omp: where omp comes from (the pinned copy tereditor downloads, or the command), the documented launch flags with their suggested values, the MCP profile and code intelligence.</summary>
internal sealed partial class OmpSettingsView : StackPanel
{
    internal OmpSettingsView(IPluginUIContext context, Func<OmpStatus?> status, Action<OmpStatus> update)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<StackPanel>("ModelSourceHost")!.Children.Add(UiSlots.ModelSourceView(context, OmpManifest.Id));
        var applying = false;
        void Update(Func<OmpSettings, OmpSettings> change) => _ = UpdateAsync(change);
        async Task UpdateAsync(Func<OmpSettings, OmpSettings> change)
        {
            try { await context.UpdateSettingsAsync(change(context.Settings<OmpSettings>())); }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change the omp setting"); }
        }

        var command = this.FindControl<TextBox>("OmpCommandInput")!;
        var model = this.FindControl<AutoCompleteBox>("OmpModel")!;
        var approval = this.FindControl<AutoCompleteBox>("OmpApprovalMode")!;
        var advisor = this.FindControl<CheckBox>("OmpAdvisor")!;
        var mcp = this.FindControl<CheckBox>("OmpMcp")!;
        var ownLsp = this.FindControl<CheckBox>("OmpOwnLsp")!;
        var source = this.FindControl<ComboBox>("OmpSource")!;
        var sourceHint = this.FindControl<TextBlock>("OmpSourceHint")!;
        var sourceNote = this.FindControl<TextBlock>("OmpSourceNote")!;
        var managedPanel = this.FindControl<StackPanel>("OmpManagedPanel")!;
        var commandPanel = this.FindControl<StackPanel>("OmpCommandPanel")!;
        var managedLine = this.FindControl<TextBlock>("OmpManagedLine")!;
        var managedError = this.FindControl<TextBlock>("OmpManagedError")!;
        var managedProgress = this.FindControl<StackPanel>("OmpManagedProgress")!;
        var download = this.FindControl<Button>("OmpManagedDownload")!;
        var remove = this.FindControl<Button>("OmpManagedRemove")!;
        DownloadStrip? strip = null;
        var polling = false;
        var attached = false;
        model.ItemsSource = OmpLaunch.ModelSuggestions;
        approval.ItemsSource = OmpLaunch.ApprovalModes;

        void Text(Control input, Func<string?> read, Func<OmpSettings, string> current, Func<OmpSettings, string, OmpSettings> write)
        {
            void Save()
            {
                var value = read()?.Trim() ?? "";
                if (!applying && value != current(context.Settings<OmpSettings>())) Update(settings => write(settings, value));
            }
            input.LostFocus += (_, _) => Save();
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); e.Handled = true; } };
        }
        Text(command, () => command.Text is { Length: > 0 } typed && typed.Trim().Length > 0 ? typed : "omp", settings => settings.Command, (settings, value) => settings with { Command = value });
        Text(model, () => model.Text, settings => settings.Model, (settings, value) => settings with { Model = value });
        Text(approval, () => approval.Text, settings => settings.ApprovalMode, (settings, value) => settings with { ApprovalMode = value });
        model.DropDownClosed += (_, _) => { if (!applying && (model.Text ?? "") != context.Settings<OmpSettings>().Model) Update(settings => settings with { Model = model.Text?.Trim() ?? "" }); };
        approval.DropDownClosed += (_, _) => { if (!applying && (approval.Text ?? "") != context.Settings<OmpSettings>().ApprovalMode) Update(settings => settings with { ApprovalMode = approval.Text?.Trim() ?? "" }); };
        advisor.Click += (_, _) => Update(settings => settings with { Advisor = advisor.IsChecked == true });
        mcp.Click += (_, _) => Update(settings => settings with { Mcp = mcp.IsChecked == true });
        ownLsp.Click += (_, _) => Update(settings => settings with { OwnCodeIntelligence = ownLsp.IsChecked == true });

        // The host does the download; the page asks for its state while one runs, so it also shows a download a launch started.
        async Task RefreshAsync(Func<Task<OmpStatus>>? request = null)
        {
            try { update(await (request?.Invoke() ?? context.RequestAsync(OmpContract.Status, new OmpNoParams()).AsTask())); }
            catch (Exception error) when (error is not OperationCanceledException) { }
            Render(context.Settings<OmpSettings>());
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
            var chosen = source.SelectedIndex == 1 ? OmpSources.System : OmpSources.Managed;
            if (!applying && chosen != Shown(context.Settings<OmpSettings>())) Update(settings => settings with { Source = chosen });
        };
        download.Click += (_, _) =>
        {
            update((status() ?? new OmpStatus(false, "omp") { Source = OmpSources.Managed }) with { Installing = true, Progress = "Looking for Bun…", Error = "" });
            Render(context.Settings<OmpSettings>());
            _ = RefreshAsync(() => context.RequestAsync(OmpContract.Install, new OmpNoParams()).AsTask());
            if (!polling) _ = PollAsync();
        };
        remove.Click += (_, _) => _ = RefreshAsync(() => context.RequestAsync(OmpContract.Remove, new OmpNoParams()).AsTask());

        // What the page shows as chosen: the user's choice, else what the host settled on (managed while Bun is there), managed before it answers.
        string Shown(OmpSettings settings) => settings.Source.Length > 0 ? settings.Source : status()?.Source ?? OmpSources.Managed;

        void RenderManaged(OmpStatus? state)
        {
            var installing = state is { Installing: true };
            var found = state?.ManagedVersion ?? "";
            var pinned = state?.PinnedVersion ?? "";
            managedLine.Text = installing ? state!.Progress
                : found.Length > 0 && found == pinned ? $"omp {found} is downloaded. It is the pinned version."
                : found.Length > 0 ? $"omp {found} is downloaded, but omp {pinned} is pinned. Update to use it."
                : pinned.Length > 0 ? $"omp {pinned} is pinned and not downloaded yet. It downloads when you first start omp."
                : "";
            managedError.Text = state?.Error ?? "";
            managedError.IsVisible = managedError.Text.Length > 0;
            if (installing && strip is null)
            {
                strip = new DownloadStrip(state!.Progress, () => _ = RefreshAsync(() => context.RequestAsync(OmpContract.CancelInstall, new OmpNoParams()).AsTask()));
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

        void Render(OmpSettings settings)
        {
            applying = true;
            var managed = Shown(settings) == OmpSources.Managed;
            source.SelectedIndex = managed ? 0 : 1;
            managedPanel.IsVisible = managed;
            commandPanel.IsVisible = !managed;
            sourceHint.Text = managed ? "Run the omp that tereditor downloads, on Bun from Runtimes" : "Run the omp on your PATH";
            sourceNote.Text = status()?.SourceNote ?? "";
            sourceNote.IsVisible = sourceNote.Text.Length > 0;
            RenderManaged(status());
            if (!command.IsFocused) command.Text = settings.Command;
            if (!model.IsKeyboardFocusWithin) model.Text = settings.Model;
            if (!approval.IsKeyboardFocusWithin) approval.Text = settings.ApprovalMode;
            advisor.IsChecked = settings.Advisor;
            mcp.IsChecked = settings.Mcp;
            ownLsp.IsChecked = settings.OwnCodeIntelligence;
            applying = false;
        }

        Render(context.Settings<OmpSettings>());
        IDisposable? subscription = null;
        var shownSource = "";
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            Render(context.Settings<OmpSettings>());
            _ = RefreshAsync();
            subscription ??= context.OnSettings<OmpSettings>(settings =>
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