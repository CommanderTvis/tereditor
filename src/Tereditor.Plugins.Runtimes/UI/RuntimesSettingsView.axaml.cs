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
    private readonly Dictionary<string, RowParts> parts = [];
    private readonly Dictionary<string, (string Text, bool Failed)> results = [];
    private bool busy;

    // What a row shows while its download runs and after it: the pieces are always laid out, so the row keeps its height.
    private sealed record RowParts(StackPanel Group, Button Download, Button Cancel, TextBlock Text, ProgressBar Bar);

    /// <summary>How often the downloaded size is read while a download runs.</summary>
    internal static TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    internal RuntimesSettingsView(IPluginUIContext context)
    {
        this.context = context;
        AvaloniaXamlLoader.Load(this);
        rows = this.FindControl<StackPanel>("RuntimesRows")!;
        error = this.FindControl<TextBlock>("RuntimesError")!;
        AttachedToVisualTree += (_, _) => _ = RunAsync(_ => context.RequestAsync(RuntimesContract.Status, new RuntimesStatusParams()).AsTask());
    }

    /// <summary>
    /// Asks the host and shows the answer. One request runs at a time. With <paramref name="doing"/> the request is a download of <paramref name="kind"/>: that row shows the
    /// progress the host reports (percent and bytes when the size is known) and Cancel, and the other controls wait until it ends. The result or error stays on the row.
    /// </summary>
    internal async Task RunAsync(Func<CancellationToken, Task<RuntimesState>> request, string? doing = null, string kind = "")
    {
        if (busy) return;
        busy = true;
        using var source = new CancellationTokenSource();
        var part = doing is not null && parts.TryGetValue(kind, out var found) ? found : null;
        var downloadWasEnabled = part?.Download.IsEnabled ?? false;
        var rendered = false;
        Task? poll = null;
        if (part is not null)
        {
            foreach (var other in parts.Values) other.Group.IsEnabled = false;
            ((TextBlock)part.Download.Content!).Text = "Downloading…";
            part.Download.IsEnabled = false;
            part.Cancel.Click += (_, _) => source.Cancel();
            part.Cancel.IsEnabled = true;
            part.Cancel.Opacity = 1;
            part.Bar.IsIndeterminate = true;
            part.Bar.Opacity = 1;
            results.Remove(kind);
            Say(part, doing + "…", false);
            Show("");
            poll = PollAsync(part, doing!, kind, source.Token);
        }
        try
        {
            var state = await request(source.Token);
            if (part is not null) results[kind] = state.Error.Length > 0 ? (state.Error, true) : (NameOf(state, kind) + " is downloaded.", false);
            Render(state, part is null);
            rendered = true;
        }
        catch (OperationCanceledException)
        {
            if (part is not null) results[kind] = ("The download was cancelled.", false); else Show("The download was cancelled.");
            try { Render(await context.RequestAsync(RuntimesContract.Status, new RuntimesStatusParams()).AsTask(), false); rendered = true; } catch (Exception) { }
        }
        catch (Exception)
        {
            if (part is not null) results[kind] = ("Couldn't reach the host. Try again in a moment.", true); else Show("Couldn't reach the host. Try again in a moment.");
        }
        finally
        {
            source.Cancel();
            if (poll is not null) await poll;
            if (part is not null && !rendered) Restore(part, kind, downloadWasEnabled);
            busy = false;
        }
    }

    // The host could not be asked again, so the row that was already on screen gets its controls back.
    private void Restore(RowParts part, string kind, bool downloadWasEnabled)
    {
        foreach (var other in parts.Values) other.Group.IsEnabled = true;
        ((TextBlock)part.Download.Content!).Text = "Download now";
        part.Download.IsEnabled = downloadWasEnabled;
        Idle(part);
        if (results.TryGetValue(kind, out var result)) Say(part, result.Text, result.Failed);
    }

    private static void Idle(RowParts part)
    {
        part.Cancel.IsEnabled = false;
        part.Cancel.Opacity = 0;
        part.Bar.IsIndeterminate = false;
        part.Bar.Value = 0;
        part.Bar.Opacity = 0;
        Say(part, "", false);
    }

    private static string NameOf(RuntimesState state, string kind) => state.Runtimes.FirstOrDefault(row => row.Kind == kind)?.Name ?? "The runtime";

    // The host counts the bytes it receives and reports them in the status, so the poll reads the real download.
    private async Task PollAsync(RowParts part, string doing, string kind, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(PollInterval, token);
                var state = await context.RequestAsync(RuntimesContract.Status, new RuntimesStatusParams(), token);
                if (state.Runtimes.FirstOrDefault(row => row.Kind == kind) is { Downloading: true } row) Report(part, doing, row);
            }
        }
        catch (Exception) { }
    }

    private static void Report(RowParts part, string doing, RuntimeRow row)
    {
        var known = row.TotalBytes > 0;
        part.Bar.IsIndeterminate = !known;
        if (known) part.Bar.Value = Math.Min(100.0, row.ReceivedBytes * 100.0 / row.TotalBytes);
        part.Text.Text = Describe(doing, row);
    }

    /// <summary>The line above the bar: the percent and both sizes when the size is known, the bytes so far when not.</summary>
    internal static string Describe(string doing, RuntimeRow row) =>
        row.TotalBytes > 0 ? $"{doing}… {Math.Min(100, row.ReceivedBytes * 100 / row.TotalBytes)}% ({Size(row.ReceivedBytes)} of {Size(row.TotalBytes)})"
        : row.ReceivedBytes > 0 ? $"{doing}… {Size(row.ReceivedBytes)}"
        : doing + "…";

    private static void Say(RowParts part, string text, bool failed)
    {
        part.Text.Text = text;
        if (failed) part.Text.Foreground = Ui.Danger;
        else part.Text.ClearValue(TextBlock.ForegroundProperty);
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

    private void Render(RuntimesState state, bool pageError)
    {
        Show(pageError ? state.Error : "");
        rows.Children.Clear();
        parts.Clear();
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

        var group = new StackPanel { Spacing = 6 };
        panel.Children.Remove(pin);
        group.Children.Add(pin);
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var download = Ui.Button("Download now", () => _ = RunAsync(token => context.RequestAsync(RuntimesContract.Download, new RuntimeKindParams(row.Kind), token).AsTask(), "Downloading " + row.Name, row.Kind));
        download.Name = "RuntimesDownload";
        download.IsEnabled = !row.Downloaded;
        var remove = Ui.Button("Remove download", () => _ = RunAsync(token => context.RequestAsync(RuntimesContract.Remove, new RuntimeKindParams(row.Kind), token).AsTask()));
        remove.Click += (_, _) => results.Remove(row.Kind);
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
        group.Children.Add(actions);
        panel.Children.Add(group);

        // The progress slot is always laid out, so the row keeps its height before, during and after a download.
        var text = new TextBlock { Name = "RuntimesProgressText", Classes = { "detail" }, MinHeight = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var cancel = Ui.Button("Cancel", () => { });
        cancel.Name = "RuntimesCancel";
        var bar = new ProgressBar { Name = "RuntimesProgressBar", Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4, Background = Ui.Hover };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(cancel, 1);
        line.Children.Add(text);
        line.Children.Add(cancel);
        var slot = new StackPanel { Spacing = 4 };
        slot.Children.Add(line);
        slot.Children.Add(bar);
        panel.Children.Add(slot);
        var part = new RowParts(group, download, cancel, text, bar);
        parts[row.Kind] = part;
        Idle(part);
        if (results.TryGetValue(row.Kind, out var result)) Say(part, result.Text, result.Failed);
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