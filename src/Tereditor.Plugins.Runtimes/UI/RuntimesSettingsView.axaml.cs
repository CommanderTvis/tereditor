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
    private readonly StackPanel progress;
    private bool busy;

    /// <summary>How often the downloaded size is read while a download runs.</summary>
    internal static TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    internal RuntimesSettingsView(IPluginUIContext context)
    {
        this.context = context;
        AvaloniaXamlLoader.Load(this);
        rows = this.FindControl<StackPanel>("RuntimesRows")!;
        error = this.FindControl<TextBlock>("RuntimesError")!;
        progress = this.FindControl<StackPanel>("RuntimesProgress")!;
        AttachedToVisualTree += (_, _) => _ = RunAsync(_ => context.RequestAsync(RuntimesContract.Status, new RuntimesStatusParams()).AsTask());
    }

    /// <summary>
    /// Asks the host and shows the answer. One request runs at a time. With <paramref name="doing"/> the request is a download: the page shows what it does with the size so far
    /// and a Cancel button, and the buttons wait until it ends.
    /// </summary>
    internal async Task RunAsync(Func<CancellationToken, Task<RuntimesState>> request, string? doing = null, string kind = "")
    {
        if (busy) return;
        busy = true;
        using var source = new CancellationTokenSource();
        DownloadStrip? strip = null;
        Task? poll = null;
        if (doing is not null)
        {
            strip = new DownloadStrip(doing + "…", source.Cancel);
            progress.Children.Add(strip);
            rows.IsEnabled = false;
            Show("");
            poll = PollAsync(strip, doing, kind, source.Token);
        }
        try { Render(await request(source.Token)); }
        catch (OperationCanceledException)
        {
            try { Render(await context.RequestAsync(RuntimesContract.Status, new RuntimesStatusParams()).AsTask()); } catch (Exception) { }
            Show("The download was cancelled.");
        }
        catch (Exception) { Show("Couldn't reach the host. Try again in a moment."); }
        finally
        {
            source.Cancel();
            if (poll is not null) await poll;
            if (strip is not null) progress.Children.Remove(strip);
            rows.IsEnabled = true;
            busy = false;
        }
    }

    // The host writes the archive into the runtime's folder while it downloads, so the size of that folder is the bytes so far.
    private async Task PollAsync(DownloadStrip strip, string doing, string kind, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(PollInterval, token);
                var state = await context.RequestAsync(RuntimesContract.Status, new RuntimesStatusParams(), token);
                if (state.Runtimes.FirstOrDefault(row => row.Kind == kind) is { } row) strip.Message = $"{doing}… {Size(row.DownloadBytes)} so far";
            }
        }
        catch (Exception) { }
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
        if (row.Downloaded && row.Source is not ("downloaded" or ""))
            panel.Children.Add(new TextBlock { Name = "RuntimesAlso", Text = "Also downloaded. It is used only when none is found on your computer.", Classes = { "detail" } });
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
        var download = Ui.Button("Download now", () => _ = RunAsync(token => context.RequestAsync(RuntimesContract.Download, new RuntimeKindParams(row.Kind), token).AsTask(), "Downloading " + row.Name, row.Kind));
        download.Name = "RuntimesDownload";
        download.IsEnabled = !row.Downloaded;
        var remove = Ui.Button("Remove download", () => _ = RunAsync(token => context.RequestAsync(RuntimesContract.Remove, new RuntimeKindParams(row.Kind), token).AsTask()));
        remove.Name = "RuntimesRemove";
        remove.IsEnabled = row.Downloaded;
        actions.Children.Add(download);
        actions.Children.Add(remove);
        if (row.Downloaded && row.DownloadedPath.Length > 0 && row.Source != "downloaded" && row.Pinned != row.DownloadedPath)
        {
            var use = Ui.Button("Use the download", () => _ = SavePinAsync(row.Kind, row.DownloadedPath));
            use.Name = "RuntimesUseDownload";
            actions.Children.Add(use);
        }
        if (row.Pinned.Length > 0)
        {
            var computer = Ui.Button("Use the one on my computer", () => _ = SavePinAsync(row.Kind, ""));
            computer.Name = "RuntimesUseComputer";
            actions.Children.Add(computer);
        }
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
        await RunAsync(token => context.RequestAsync(RuntimesContract.Status, new RuntimesStatusParams(), token).AsTask());
    }
}