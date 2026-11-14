using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Processes.UI;

internal sealed partial class ProcessesSettingsView : StackPanel
{
    internal ProcessesSettingsView(IPluginUIContext context)
    {
        AvaloniaXamlLoader.Load(this);
        var stopChildren = this.FindControl<CheckBox>("ProcessesStopAgentChildren")!;
        stopChildren.Click += (_, _) => _ = UpdateAsync(stopChildren.IsChecked == true);
        async Task UpdateAsync(bool value)
        {
            try { await context.UpdateSettingsAsync(context.Settings<ProcessesSettings>() with { StopAgentChildren = value }); }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change the Processes setting"); }
        }

        async Task Save(Func<ProcessesSettings, ProcessesSettings> change)
        {
            try { await context.UpdateSettingsAsync(change(context.Settings<ProcessesSettings>())); }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change the Processes setting"); }
        }

        var guard = this.FindControl<Switch>("ProcessesMemoryGuard")!;
        var limits = this.FindControl<StackPanel>("ProcessesMemoryLimits")!;
        guard.CheckedChange += on => { if (on != context.Settings<ProcessesSettings>().MemoryGuard) _ = Save(settings => settings with { MemoryGuard = on }); };
        var syncing = false;
        // A limit is automatic (0) or a fixed size in GB, shown as one choice list.
        Action<ProcessesSettings> Limit(string name, string automatic, int[] gigabytes, Func<ProcessesSettings, int> read, Func<ProcessesSettings, int, ProcessesSettings> write)
        {
            var box = this.FindControl<ComboBox>(name)!;
            var sizes = new[] { 0 }.Concat(gigabytes.Select(gb => gb * 1024)).ToArray();
            box.ItemsSource = new[] { automatic }.Concat(gigabytes.Select(gb => gb + " GB")).ToArray();
            box.SelectionChanged += (_, _) =>
            {
                if (syncing || box.SelectedIndex < 0) return;
                var size = sizes[box.SelectedIndex];
                if (read(context.Settings<ProcessesSettings>()) != size) _ = Save(settings => write(settings, size));
            };
            return settings => box.SelectedIndex = Math.Max(0, Array.IndexOf(sizes, read(settings)));
        }
        var showPerTerminal = Limit("ProcessesMemoryPerTerminal", "Automatic: a quarter of physical memory, 2 to 32 GB", [2, 4, 8, 16, 32, 64], settings => settings.PerTerminalMb, (settings, size) => settings with { PerTerminalMb = size });
        var showTotal = Limit("ProcessesMemoryTotal", "Automatic: 60% of physical memory", [8, 16, 32, 64, 128], settings => settings.TotalMb, (settings, size) => settings with { TotalMb = size });
        var protect = this.FindControl<TextBox>("ProcessesMemoryProtect")!;
        string Names(ProcessesSettings settings) => string.Join(", ", settings.Protect);
        void Commit()
        {
            var names = (protect.Text ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (string.Join(", ", names) != Names(context.Settings<ProcessesSettings>())) _ = Save(settings => settings with { Protect = names });
        }
        protect.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { protect.Text = Names(context.Settings<ProcessesSettings>()); e.Handled = true; }
            else if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        };
        protect.LostFocus += (_, _) => Commit();

        // Settings arrive with the host's snapshot; the controls update in place.
        void Render(ProcessesSettings settings)
        {
            stopChildren.IsChecked = settings.StopAgentChildren;
            syncing = true;
            try
            {
                guard.IsChecked = settings.MemoryGuard;
                limits.IsVisible = settings.MemoryGuard;
                showPerTerminal(settings);
                showTotal(settings);
                if (!protect.IsFocused) protect.Text = Names(settings);
            }
            finally { syncing = false; }
        }
        Render(context.Settings<ProcessesSettings>());
        IDisposable? subscription = null;
        AttachedToVisualTree += (_, _) =>
        {
            Render(context.Settings<ProcessesSettings>());
            subscription ??= context.OnSettings<ProcessesSettings>(Render);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            subscription?.Dispose();
            subscription = null;
        };
    }
}