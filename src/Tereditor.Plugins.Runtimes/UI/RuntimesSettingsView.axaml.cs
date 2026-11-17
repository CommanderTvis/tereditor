using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Runtimes.UI;

internal sealed partial class RuntimesSettingsView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly StackPanel rows;
    private readonly TextBlock error;
    private bool busy;

    internal RuntimesSettingsView(IPluginUIContext context)
    {
        this.context = context;
        AvaloniaXamlLoader.Load(this);
        rows = this.FindControl<StackPanel>("RuntimesRows")!;
        error = this.FindControl<TextBlock>("RuntimesError")!;
        AttachedToVisualTree += (_, _) => _ = RunAsync(() => context.RequestAsync(RuntimesContract.Status, new RuntimesStatusParams()).AsTask());
    }

    /// <summary>Asks the host and shows the answer. One request runs at a time, so a click during a download changes nothing.</summary>
    private async Task RunAsync(Func<Task<RuntimesState>> request)
    {
        if (busy) return;
        busy = true;
        try { Render(await request()); }
        catch (Exception) { Show("Couldn't reach the host. Try again in a moment."); }
        finally { busy = false; }
    }

    private void Show(string text)
    {
        error.Text = text;
        error.IsVisible = text.Length > 0;
    }

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes + " bytes",
        < 1024 * 1024 => (bytes / 1024) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024 * 1024)) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB"
    };

    internal static string InUse(RuntimeRow row)
    {
        var version = row.Version.Length > 0 ? ", version " + row.Version : "";
        return row.Source switch
        {
            "pinned" => $"In use: the program you chose ({row.Path}){version}.",
            "system" => $"In use: the one found on your computer ({row.Path}){version}.",
            "downloaded" => $"In use: the copy tereditor downloaded{version}.",
            _ => row.Message
        };
    }

    private void Render(RuntimesState state)
    {
        Show(state.Error);
        rows.Children.Clear();
        foreach (var row in state.Runtimes) rows.Children.Add(Row(row));
    }

    private Control Row(RuntimeRow row)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = row.Name, FontSize = 14, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = InUse(row) });
        if (row.Source.Length > 0 && row.Message.Length > 0) panel.Children.Add(new TextBlock { Text = row.Message, Classes = { "detail" } });
        panel.Children.Add(new TextBlock { Text = row.Downloaded || row.DownloadBytes > 0 ? "Downloaded files take " + Size(row.DownloadBytes) + " on disk." : "Nothing is downloaded.", Classes = { "detail" } });

        var pin = new TextBox { Text = row.Pinned, PlaceholderText = "Full path of the program, to use it instead" };
        Avalonia.Automation.AutomationProperties.SetName(pin, row.Name + " path");
        void Commit()
        {
            var path = (pin.Text ?? "").Trim();
            if (path == row.Pinned) return;
            _ = SavePinAsync(row.Kind, path);
        }
        pin.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { pin.Text = row.Pinned; e.Handled = true; }
            else if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        };
        pin.LostFocus += (_, _) => Commit();
        panel.Children.Add(new TextBlock { Text = "Use this program instead", Classes = { "detail" } });
        panel.Children.Add(pin);

        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var download = Ui.Button("Download now", () => _ = RunAsync(() => context.RequestAsync(RuntimesContract.Download, new RuntimeKindParams(row.Kind)).AsTask()));
        download.IsEnabled = !row.Downloaded;
        var remove = Ui.Button("Remove download", () => _ = RunAsync(() => context.RequestAsync(RuntimesContract.Remove, new RuntimeKindParams(row.Kind)).AsTask()));
        remove.IsEnabled = row.Downloaded;
        actions.Children.Add(download);
        actions.Children.Add(remove);
        panel.Children.Add(actions);
        return panel;
    }

    private async Task SavePinAsync(string kind, string path)
    {
        try
        {
            var settings = context.Settings<RuntimesSettings>();
            await context.UpdateSettingsAsync(kind == "bun" ? settings with { BunPath = path } : settings with { UvPath = path });
        }
        catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't save the path"); return; }
        await RunAsync(() => context.RequestAsync(RuntimesContract.Status, new RuntimesStatusParams()).AsTask());
    }
}