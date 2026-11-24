using System.Reflection;

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Runtimes;
using Tereditor.Plugins.Runtimes.UI;

using Kit = Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks;

// Settings > Runtimes rendered headless over a fake host: the rows, the buttons and each state (not installed, start, midway with percent and bytes, cancelled, failed, done,
// the download that is not the one in use). "Off" has no page: a plugin that is off registers no section, which the first step asserts.
internal static class RuntimesUiChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("runtimes-ui: " + message);
    }

    private static RuntimeRow Row(string kind, string source = "", string pinned = "", bool downloaded = false, long bytes = 0, string downloadedPath = "", string message = "", string otherPath = "", string problem = "") =>
        new(kind, kind == "bun" ? "Bun" : "uv", source, source.Length > 0 ? "/x/" + kind : "", source.Length > 0 ? "1.4.4" : "", pinned, bytes, downloaded, message)
        { DownloadedPath = downloadedPath, DownloadedVersion = downloaded ? "1.4.4" : "", OtherPath = otherPath, OtherVersion = otherPath.Length > 0 ? "1.3.0" : "", DownloadProblem = problem };

    internal static void Run(string root)
    {
        var state = new RuntimesState([Row("bun", message: "Bun is not installed. It is downloaded the first time a plugin needs it."), Row("uv", "system")]);
        var pending = new TaskCompletionSource<RuntimesState>();
        CancellationToken downloadToken = default;
        long received = 0, total = 0;
        var downloading = false;
        var saved = new List<RuntimesSettings>();
        var fake = Fake.Create(async (method, parameters, token) =>
        {
            switch (method)
            {
                case "status": return state with { Runtimes = [.. state.Runtimes.Select(row => row.Kind == "bun" && downloading ? row with { Downloading = true, ReceivedBytes = received, TotalBytes = total } : row)] };
                case "download": downloadToken = token; return await pending.Task.WaitAsync(token);
                default: throw new NotSupportedException(method);
            }
        }, saved);
        Require(Fake.Sections == 0, "a plugin that is off registers no section");
        new RuntimesUI().Activate(fake);
        Require(Fake.Sections == 1, "the plugin registers its page");
        RuntimesSettingsView.PollInterval = TimeSpan.FromMilliseconds(20);
        var view = Fake.View!;
        var window = new Window { Width = 700, Height = 900, Content = new ScrollViewer { Content = view } };
        window.Show();
        void Settle() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        void Until(Func<bool> done, string what)
        {
            for (var tries = 0; tries < 300 && !done(); tries++) { Settle(); Thread.Sleep(10); }
            Require(done(), what);
        }
        Button? Find(string name) => view.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == name);
        T? Named<T>(string name) where T : Control => view.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);
        try
        {
            Until(() => view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Bun"), "the page shows the Bun row");
            Require(view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "uv"), "the page shows the uv row");
            // Not installed: Download now is on, Remove download is off, and the message says where it comes from.
            Require(Find("RuntimesDownload")!.IsEnabled && !Find("RuntimesRemove")!.IsEnabled, "not installed: Download now on, Remove download off");
            Require(view.GetVisualDescendants().OfType<TextBlock>().Any(text => (text.Text ?? "").Contains("Bun is not installed", StringComparison.Ordinal)), "not installed: the message is shown");
            Control Slot() { Settle(); return (Control)Named<ProgressBar>("RuntimesProgressBar")!.Parent!; }
            string Label() => ((TextBlock)Find("RuntimesDownload")!.Content!).Text ?? "";
            string Line() => Named<TextBlock>("RuntimesProgressText")?.Text ?? "";
            var slotHeight = Slot().Bounds.Height;
            Require(slotHeight > 0 && Named<ProgressBar>("RuntimesProgressBar")!.Opacity == 0 && !Find("RuntimesCancel")!.IsEnabled && Line().Length == 0 && Label() == "Download now", "idle: the progress slot is laid out but quiet");

            // Start: the button says so and waits, the bar and Cancel show on that row, the row keeps its height.
            Find("RuntimesDownload")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => Label() == "Downloading…", "start: the button says Downloading…");
            Require(!Find("RuntimesDownload")!.IsEffectivelyEnabled && !Find("RuntimesRemove")!.IsEffectivelyEnabled && Find("RuntimesCancel") is { IsEnabled: true, Opacity: 1 }, "start: Download now and Remove download wait, Cancel is on");
            Require(Named<ProgressBar>("RuntimesProgressBar") is { Opacity: 1, IsIndeterminate: true } && Line() == "Downloading Bun…", "start: an indeterminate bar and the plain line: " + Line());
            Require(Slot().Bounds.Height == slotHeight, "start: the progress slot keeps its height");

            // Midway, size unknown: the bytes so far, the bar stays indeterminate.
            (downloading, received, total) = (true, 3L * 1024 * 1024, 0);
            Until(() => Line().Contains("3 MB", StringComparison.Ordinal), "midway: the line shows the bytes so far: " + Line());
            Require(Named<ProgressBar>("RuntimesProgressBar")!.IsIndeterminate && !Line().Contains('%'), "midway, size unknown: indeterminate, no percent");

            // Midway, size known: percent and both sizes, a determinate bar.
            (received, total) = (4L * 1024 * 1024, 10L * 1024 * 1024);
            Until(() => Line() == "Downloading Bun… 40% (4 MB of 10 MB)", "midway, size known: percent and sizes: " + Line());
            Require(Named<ProgressBar>("RuntimesProgressBar") is { IsIndeterminate: false, Value: 40 } && Slot().Bounds.Height == slotHeight, "midway, size known: a determinate bar at 40 and the same height");

            // Cancel stops the request and says so on the row; the page is usable again.
            Find("RuntimesCancel")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => downloadToken.IsCancellationRequested && Line() == "The download was canceled.", "cancel: the request is canceled and the row says so: " + Line());
            Require(Label() == "Download now" && Find("RuntimesDownload")!.IsEffectivelyEnabled && Named<ProgressBar>("RuntimesProgressBar")!.Opacity == 0 && !Find("RuntimesCancel")!.IsEnabled && Slot().Bounds.Height == slotHeight, "cancel: the row is idle again at the same height");

            // Failed: the host's error shows on the row, in the danger colour.
            pending = new();
            (downloading, received, total) = (false, 0, 0);
            Find("RuntimesDownload")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => Label() == "Downloading…", "failed: a second download starts");
            pending.SetResult(state with { Error = "Bun could not be downloaded: offline." });
            Until(() => Line() == "Bun could not be downloaded: offline.", "failed: the error shows on the row: " + Line());
            Require(Named<TextBlock>("RuntimesProgressText")!.Foreground == Kit.Ui.Danger && Label() == "Download now" && Find("RuntimesDownload")!.IsEffectivelyEnabled && Slot().Bounds.Height == slotHeight, "failed: danger color, button back, same height");

            // Done: the row says so, Remove download is on, Download now is off, the size shows.
            pending = new();
            state = new([Row("bun", "downloaded", downloaded: true, bytes: 225L * 1024 * 1024, downloadedPath: "/cache/bun"), Row("uv", "system")]);
            Find("RuntimesDownload")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => Label() == "Downloading…", "done: a download starts");
            pending.SetResult(state);
            Until(() => Line() == "Bun is downloaded." && !Find("RuntimesDownload")!.IsEnabled && Find("RuntimesRemove")!.IsEnabled, "done: the row says so, Download now off, Remove download on: " + Line());
            Require(Named<TextBlock>("RuntimesProgressText")!.Foreground != Kit.Ui.Danger && Slot().Bounds.Height == slotHeight, "done: normal color and the same height");
            Require(view.GetVisualDescendants().OfType<TextBlock>().Any(text => (text.Text ?? "").Contains("225 MB", StringComparison.Ordinal)) && Named<TextBlock>("RuntimesAlso") is null && Find("RuntimesClearPin") is null,
                "installed and in use: the size shows, no 'also downloaded' line");

            // Downloaded and verified wins over the computer's: both lines say so, nothing offers to switch.
            state = new([Row("bun", "downloaded", downloaded: true, bytes: 225L * 1024 * 1024, downloadedPath: "/cache/bun", otherPath: "/opt/homebrew/bin/bun"), Row("uv", "system")]);
            var again = Fake.Refresh!;
            again();
            Until(() => Named<TextBlock>("RuntimesAlso") is not null, "downloaded wins: the page names the computer's copy");
            string Texts() => string.Join("\n", view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            Require(Texts().Contains("In use: the downloaded copy, version 1.4.4 (verified).", StringComparison.Ordinal) && (Named<TextBlock>("RuntimesAlso")!.Text ?? "") == "Also found on your computer: /opt/homebrew/bin/bun, version 1.3.0. It is not used."
                && Named<TextBlock>("RuntimesAlsoDownloaded") is null && Find("RuntimesRemove")!.Content is Control && Find("RuntimesClearPin") is null,
                "downloaded wins: the in-use line and the 'also found' line: " + Texts());
            Require(((TextBlock)Find("RuntimesRemove")!.Content!).Text == "Remove download", "the button keeps its text");

            // A path of the user's wins; the download is beside it and a button clears the path.
            state = new([Row("bun", "pinned", pinned: "/my/bun", downloaded: true, bytes: 1, downloadedPath: "/cache/bun"), Row("uv", "system")]);
            again();
            Until(() => Named<TextBlock>("RuntimesAlsoDownloaded") is not null && Find("RuntimesClearPin") is not null, "pinned: the download is named beside it and the path can be cleared");
            Require((Named<TextBlock>("RuntimesAlsoDownloaded")!.Text ?? "") == "Also downloaded, version 1.4.4. It is not used.", "pinned: the also-downloaded line");
            Find("RuntimesClearPin")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => saved.Count == 1 && saved[0].BunPath == "", "Stop using my path clears the pin");

            // A download that failed its check is not used: a red line, the computer's copy in use, Remove download on.
            state = new([Row("bun", "system", downloaded: true, bytes: 1, downloadedPath: "/cache/bun", problem: "The downloaded Bun failed its checksum check, so it is not used."), Row("uv", "system")]);
            again();
            Until(() => Named<TextBlock>("RuntimesDownloadProblem") is not null, "damaged: the row shows the error");
            Require(Named<TextBlock>("RuntimesDownloadProblem")!.Foreground == Kit.Ui.Danger && Find("RuntimesRemove")!.IsEnabled && Named<TextBlock>("RuntimesAlsoDownloaded") is null && Texts().Contains("In use: the one found on your computer", StringComparison.Ordinal), "damaged: danger color, Remove download on, the computer's copy in use");
        }
        finally { window.Close(); }
        Prompts();
        Console.WriteLine("PASS runtimes-ui: rows, not installed, start, midway (size unknown and known), canceled, failed, done, steady row height, the download in use beside a computer install, a path of the user's, a failed check");
    }

    // The consent prompt: one per waiting download, with Download and Not now; an answer takes it down; the same list never shows a second prompt.
    private static void Prompts()
    {
        var shown = new List<(PluginPrompt Prompt, bool[] Disposed)>();
        var answers = new List<RuntimeAnswerParams>();
        var context = Fake.Create(async (method, parameters, _) =>
        {
            if (method != "answer") throw new NotSupportedException(method);
            answers.Add((RuntimeAnswerParams)parameters);
            await Task.Yield();
            return new RuntimesState([]);
        }, [], shown);
        var prompts = new RuntimeConsentPrompts(context);
        var bun = new RuntimeAsk("bun", "Bun", "1.4.4");
        prompts.Apply(new([bun]));
        Require(shown.Count == 1 && prompts.Shown == 1, "a waiting download shows one prompt");
        var text = shown[0].Prompt;
        Require(text.Title == "Download Bun?" && text.Message.Contains("Bun 1.4.4", StringComparison.Ordinal) && text.Message.Contains("Nothing is downloaded until you say yes", StringComparison.Ordinal) && !text.Message.Contains((char)8212),
            "the prompt names the runtime and its version and says nothing downloads without a yes: " + text.Message);
        Require(text.Actions.Select(action => action.Label).SequenceEqual(["Download", "Not now"]), "the prompt offers Download and Not now");
        prompts.Apply(new([bun]));
        Require(shown.Count == 1, "the same waiting download does not show a second prompt");
        prompts.Apply(new([bun, new("uv", "uv", "0.13.9")]));
        Require(shown.Count == 2 && prompts.Shown == 2, "a second runtime gets its own prompt");
        prompts.Apply(new([new("uv", "uv", "0.13.9")]));
        Require(shown[0].Disposed[0] && !shown[1].Disposed[0] && prompts.Shown == 1, "an answer from another client takes the prompt down here");
        shown[1].Prompt.Actions[1].RunAsync().AsTask().GetAwaiter().GetResult();
        Require(answers.Count == 1 && answers[0] == new RuntimeAnswerParams("uv", false), "Not now sends a no for that runtime");
        prompts.Dispose();
        Require(shown.All(item => item.Disposed[0]), "closing the plugin takes every prompt down");
    }

    private class Fake : DispatchProxy
    {
        private static Func<string, object, CancellationToken, Task<RuntimesState>> handler = null!;
        private static List<RuntimesSettings> saved = [];
        internal static int Sections;
        internal static Control? View;
        internal static Action? Refresh;

        private static List<(PluginPrompt Prompt, bool[] Disposed)> prompts = [];

        internal static IPluginUIContext Create(Func<string, object, CancellationToken, Task<RuntimesState>> backing, List<RuntimesSettings> settings, List<(PluginPrompt Prompt, bool[] Disposed)>? shown = null)
        {
            (handler, saved, Sections, View, prompts) = (backing, settings, 0, null, shown ?? []);
            return Create<IPluginUIContext, Fake>();
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Id": return RuntimesPlugin.Id;
                case "SettingsSection":
                    Sections++;
                    View = ((SettingsSectionRegistration)args![0]!).Create();
                    var view = View;
                    Refresh = () => _ = ((RuntimesSettingsView)view).RunAsync(token => handler("status", new RuntimesStatusParams(), token));
                    return null;
                case "RequestAsync":
                    {
                        var task = handler(((PluginMethodSpec)args![0]!).Name, args[1]!, (CancellationToken)args[2]!);
                        // An answer returns the (now empty) list of waiting downloads, not a state.
                        if (((PluginMethodSpec)args[0]!).Name == "answer") return Activator.CreateInstance(method.ReturnType, new RuntimeConsents([]));
                        return Activator.CreateInstance(method.ReturnType, task);
                    }
                case "Subscribe": return new Stop(new bool[1]);
                case "Prompt":
                    {
                        var disposed = new bool[1];
                        prompts.Add(((PluginPrompt)args![0]!, disposed));
                        return new Stop(disposed);
                    }
                case "Settings": return new RuntimesSettings { BunPath = saved.LastOrDefault()?.BunPath ?? "" };
                case "UpdateSettingsAsync": saved.Add((RuntimesSettings)args![0]!); return method.ReturnType == typeof(ValueTask) ? default(ValueTask) : Activator.CreateInstance(method.ReturnType, saved[^1]);
                case "Notify": throw new InvalidOperationException("runtimes-ui: the page reported an error: " + string.Join(" / ", args!.OfType<string>()));
                default: throw new NotSupportedException(method.Name);
            }
        }

        private sealed class Stop(bool[] disposed) : IDisposable
        {
            public void Dispose() => disposed[0] = true;
        }
    }
}