using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Omp;
using Tereditor.Plugins.Omp.UI;
using Tereditor.Plugins.Pi;
using Tereditor.Plugins.Pi.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks;

/// <summary>
/// The parts the Pi and omp plugins draw with the same code (<c>Tereditor.Plugins.Agent.UI</c>): the settings pages, which ask the host to
/// download only on a click on Download, and the first row of their panes.
/// </summary>
internal static class AgentUiChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("agent ui: " + message);
    }

    internal static void PiSettings()
    {
        var host = new Fake<PiSettings>(new(), new PiStatus(false, "managed") { Source = PiSources.Managed, PinnedVersion = "1.0.0" });
        PiStatus? status = null;
        var page = new PiSettingsView(host.Context, () => status, read => status = read);
        Settings(page, host, "Pi", PiContract.Status.Name, PiContract.Install.Name, settings => settings.Command, new() { Source = PiSources.Managed });
    }

    internal static void OmpSettings()
    {
        var host = new Fake<OmpSettings>(new(), new OmpStatus(false, "omp") { Source = OmpSources.Managed, PinnedVersion = "1.0.0" });
        OmpStatus? status = null;
        var page = new OmpSettingsView(host.Context, () => status, read => status = read);
        Settings(page, host, "Omp", OmpContract.Status.Name, OmpContract.Install.Name, settings => settings.Command, new() { Source = OmpSources.Managed });
    }

    private static void Settings<TSettings>(Control page, Fake<TSettings> host, string prefix, string status, string install,
        Func<TSettings, string> command, TSettings managed) where TSettings : class
    {
        var window = new Window { Width = 600, Height = 1400, Content = new ScrollViewer { Content = page } };
        window.Show();
        try
        {
            void Pump()
            {
                for (var pass = 0; pass < 3; pass++) Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            T Named<T>(string name) where T : Control => page.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
            void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Require(host.Asked.Count > 0 && host.Asked.All(method => method == status), prefix + ": showing the page only reads the state.");
            var badge = page.GetVisualDescendants().OfType<Border>().Single(border => border.Child is TextBlock { Text: "Early support" });
            Require(badge.CornerRadius.TopLeft == 4 && badge.Padding.Left == 4 && badge.Bounds.Height < 20, prefix + $": the settings page wears the compact badge: {badge.Bounds}.");
            var cards = Named<OptionCards>("Source");
            Require(cards.Options.Count == 2, prefix + ": the source is a choice of two cards.");

            var box = Named<TextBox>("CommandInput");
            box.Text = "  ";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Pump();
            Require(command(host.Settings) == prefix.ToLowerInvariant() && host.Saved == 0, prefix + ": a blank command stands for the default command.");
            box.Text = " /opt/agent ";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Pump();
            Require(command(host.Settings) == "/opt/agent", prefix + ": Enter saves the trimmed command.");

            host.Change(managed);
            Pump();
            var panel = Named<StackPanel>("ManagedPanel");
            Require(panel.IsVisible && !Named<StackPanel>("CommandPanel").IsVisible, prefix + ": the managed source shows the download in place of the command.");
            Require(Named<TextBlock>("ManagedLine").Text!.EndsWith("is pinned and not downloaded yet. Press Download to get it.", StringComparison.Ordinal),
                prefix + ": the page says that only Download gets the pinned copy: " + Named<TextBlock>("ManagedLine").Text);
            Require(host.Asked.All(method => method == status), prefix + ": choosing the managed source downloads nothing.");
            Click(Named<Button>("ManagedDownload"));
            Pump();
            Require(host.Asked.Count(method => method == install) == 1 && host.Asked.All(method => method == status || method == install), prefix + ": a click on Download asks for the download once.");
        }
        finally { window.Close(); }
    }

    // Pi's pane reads again on its reload button, which shares the first row with the title and the badge.
    internal static void PiPaneHeader()
    {
        var host = new Fake<PiSettings>(new(), new PiStatus(true, "pi"));
        var panel = new PiConfigPanel(host.Context, "workspace");
        var window = new Window { Width = 260, Height = 400, Content = panel };
        window.Show();
        try
        {
            void Until(Func<bool> ready, string message)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!ready())
                {
                    Require(DateTime.UtcNow < deadline, message);
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(5);
                }
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            int Reads(string method) { lock (host.Asked) return host.Asked.Count(asked => asked == method); }
            Until(() => Reads(PiContract.Config.Name) == 1, "The pane reads the workspace when it is shown.");
            var button = panel.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "PiRefresh");
            var heading = panel.GetVisualDescendants().OfType<StackPanel>().Single(control => control.Name == "PiHeading");
            var top = button.TranslatePoint(default, panel)!.Value;
            var row = heading.TranslatePoint(default, panel)!.Value;
            Require(top.Y < row.Y + heading.Bounds.Height && top.Y + button.Bounds.Height > row.Y && top.X >= row.X + heading.Bounds.Width && top.X + button.Bounds.Width <= 260,
                "The reload button shares the first row, at its right end.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => Reads(PiContract.Config.Name) == 2 && Reads(PiContract.Status.Name) == 2, "The reload button reads the workspace and the version again.");
            lock (host.Asked) Require(host.Asked.All(method => method == PiContract.Config.Name || method == PiContract.Status.Name), "The pane downloads nothing.");
        }
        finally { window.Close(); }
    }

    // A stand-in for the app: it holds the settings, records the methods asked for and answers each with one status or an empty snapshot.
    private sealed class Fake<TSettings> where TSettings : class
    {
        private Action<TSettings>? changed;

        internal Fake(TSettings settings, object status)
        {
            Settings = settings;
            var context = DispatchProxy.Create<IPluginUIContext, PiChecks.Stub>();
            ((PiChecks.Stub)(object)context).Handle = (method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IPluginUIContext.Settings):
                        return Settings;
                    case nameof(IPluginUIContext.OnSettings):
                        changed = (Action<TSettings>)args![0]!;
                        return Stream.Null;
                    case nameof(IPluginUIContext.UpdateSettingsAsync):
                        Saved++;
                        Settings = (TSettings)args![0]!;
                        return ValueTask.CompletedTask;
                    case nameof(IPluginUIContext.RequestAsync):
                        var name = ((PluginMethodSpec)args![0]!).Name;
                        lock (Asked) Asked.Add(name);
                        var result = method.ReturnType.GetGenericArguments()[0];
                        var answer = result.IsInstanceOfType(status) ? status : new PiConfigSnapshot("/work", "/agent", "trusted", "/agent/trust.json", [], [], [], false);
                        return Activator.CreateInstance(method.ReturnType, answer);
                }
                if (method.ReturnType == typeof(Control)) return new Control();
                if (method.ReturnType == typeof(IDisposable)) return Stream.Null;
                return method.ReturnType == typeof(void) || !method.ReturnType.IsValueType ? null : Activator.CreateInstance(method.ReturnType);
            };
            Context = context;
        }

        internal IPluginUIContext Context { get; }
        internal TSettings Settings { get; private set; }
        internal List<string> Asked { get; } = [];
        internal int Saved { get; private set; }

        /// <summary>The host's settings changed, as another client or the saved choice would report it.</summary>
        internal void Change(TSettings settings)
        {
            Settings = settings;
            changed!(settings);
        }
    }
}