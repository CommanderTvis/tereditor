using System.Reflection;

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Runtimes;
using Tereditor.Plugins.Runtimes.UI;

namespace Tereditor.Checks;

// Settings > Runtimes rendered headless over a fake host: the rows, the buttons and each state (not installed, downloading with its size and Cancel, installed, failed,
// the download that is not the one in use). "Off" has no page: a plugin that is off registers no section, which the first step asserts.
internal static class RuntimesUiChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("runtimes-ui: " + message);
    }

    private static RuntimeRow Row(string kind, string source = "", string pinned = "", bool downloaded = false, long bytes = 0, string downloadedPath = "", string message = "") =>
        new(kind, kind == "bun" ? "Bun" : "uv", source, source.Length > 0 ? "/x/" + kind : "", source.Length > 0 ? "1.4.4" : "", pinned, bytes, downloaded, message) { DownloadedPath = downloadedPath };

    internal static void Run(string root)
    {
        var state = new RuntimesState([Row("bun", message: "Bun is not installed. It is downloaded the first time a plugin needs it."), Row("uv", "system")]);
        var pending = new TaskCompletionSource<RuntimesState>();
        CancellationToken downloadToken = default;
        long size = 0;
        var saved = new List<RuntimesSettings>();
        var fake = Fake.Create(async (method, parameters, token) =>
        {
            switch (method)
            {
                case "status": return state with { Runtimes = [.. state.Runtimes.Select(row => row.Kind == "bun" && size > 0 ? row with { DownloadBytes = size } : row)] };
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
            Require(Named<StackPanel>("DownloadStrip") is null, "no progress before a download starts");

            // Downloading: progress with the size so far, Cancel, and the buttons wait.
            Find("RuntimesDownload")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => Named<StackPanel>("DownloadStrip") is not null, "downloading: the progress strip appears");
            size = 3 * 1024 * 1024;
            Until(() => (Named<TextBlock>("DownloadText")?.Text ?? "").Contains("3 MB so far", StringComparison.Ordinal), "downloading: the strip shows the bytes so far: " + Named<TextBlock>("DownloadText")?.Text);
            Require(Named<ProgressBar>("DownloadProgress") is { IsVisible: true } && Find("DownloadCancel") is { IsEnabled: true }, "downloading: a bar and an enabled Cancel");
            Require(!Find("RuntimesDownload")!.IsEffectivelyEnabled && !Find("RuntimesRemove")!.IsEffectivelyEnabled, "downloading: Download now and Remove download wait");

            // Cancel stops the request and says so; the page is usable again.
            Find("DownloadCancel")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => downloadToken.IsCancellationRequested && Named<StackPanel>("DownloadStrip") is null, "cancel: the request is cancelled and the strip goes away");
            Require(Named<TextBlock>("RuntimesError") is { IsVisible: true } cancelled && (cancelled.Text ?? "").Contains("cancelled", StringComparison.Ordinal) && Find("RuntimesDownload")!.IsEffectivelyEnabled, "cancel: the page says so and works again");

            // Failed: the host's error shows under the rows.
            pending = new();
            Find("RuntimesDownload")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => Named<StackPanel>("DownloadStrip") is not null, "failed: a second download starts");
            pending.SetResult(state with { Error = "Bun could not be downloaded: offline." });
            Until(() => Named<StackPanel>("DownloadStrip") is null && Named<TextBlock>("RuntimesError") is { IsVisible: true } failed && (failed.Text ?? "").Contains("offline", StringComparison.Ordinal), "failed: the error shows and the strip is gone");

            // Installed and in use: Remove download is on, Download now is off, the size shows.
            pending = new();
            size = 0;
            state = new([Row("bun", "downloaded", downloaded: true, bytes: 225L * 1024 * 1024, downloadedPath: "/cache/bun"), Row("uv", "system")]);
            Find("RuntimesDownload")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => Named<StackPanel>("DownloadStrip") is not null, "installed: a download starts to refresh the page");
            pending.SetResult(state);
            Until(() => Named<StackPanel>("DownloadStrip") is null && !Find("RuntimesDownload")!.IsEnabled && Find("RuntimesRemove")!.IsEnabled, "installed: Download now off, Remove download on");
            Require(view.GetVisualDescendants().OfType<TextBlock>().Any(text => (text.Text ?? "").Contains("225 MB", StringComparison.Ordinal)) && Named<TextBlock>("RuntimesAlso") is null && Find("RuntimesUseDownload") is null,
                "installed and in use: the size shows, no 'also downloaded' line");

            // The download exists but the one on the computer wins: the page says so and offers both ways round.
            state = new([Row("bun", "system", downloaded: true, bytes: 225L * 1024 * 1024, downloadedPath: "/cache/bun"), Row("uv", "system")]);
            var again = Fake.Refresh!;
            again();
            Until(() => Named<TextBlock>("RuntimesAlso") is not null, "system wins: the page says the download is used only when none is found");
            Require((Named<TextBlock>("RuntimesAlso")!.Text ?? "").Contains("only when none is found on your computer", StringComparison.Ordinal) && Find("RuntimesUseDownload") is not null && Find("RuntimesUseComputer") is null,
                "system wins: Use the download is offered, Use the one on my computer is not (nothing is pinned)");
            Find("RuntimesUseDownload")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => saved.Count == 1, "Use the download saves a setting");
            Require(saved[0].BunPath == "/cache/bun", "Use the download pins the downloaded program: " + saved[0].BunPath);
            state = new([Row("bun", "pinned", pinned: "/cache/bun", downloaded: true, bytes: 1, downloadedPath: "/cache/bun"), Row("uv", "system")]);
            again();
            Until(() => Find("RuntimesUseComputer") is not null && Find("RuntimesUseDownload") is null, "pinned: Use the one on my computer replaces Use the download");
            Find("RuntimesUseComputer")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => saved.Count == 2 && saved[1].BunPath == "", "Use the one on my computer clears the pin");
        }
        finally { window.Close(); }
        Prompts();
        Console.WriteLine("PASS runtimes-ui: rows, not installed, downloading with size and Cancel, cancelled, failed, installed, download beside a system install, use the download");
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