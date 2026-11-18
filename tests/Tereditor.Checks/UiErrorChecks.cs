using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Tereditor.UI;

namespace Tereditor.Checks;

/// <summary>A throwing click handler is logged, noticed once, and leaves the dispatcher running; startup, corrupt state and loops still fail.</summary>
internal static class UiErrorChecks
{
    private static void Boom() => throw new InvalidOperationException("boom from a click");

    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "ui-errors");
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "ui-errors.log");
        long now = 1000;
        var notices = new List<UiErrorNotice>();
        var guard = new UiErrorGuard(log, Path.Combine(directory, "crash.log"), () => now) { Running = true, Notify = notices.Add };
        guard.InstallDispatcherHook();
        try
        {
            var button = new Button();
            button.Click += (_, _) => Boom();
            var window = new Window { Content = button };
            window.Show();
            void Click() => Dispatcher.UIThread.Post(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Send);

            Click(); Dispatcher.UIThread.RunJobs();
            var ran = false;
            Dispatcher.UIThread.Post(() => ran = true); Dispatcher.UIThread.RunJobs();
            guard.Flush();
            var text = File.ReadAllText(log);
            Require(text.Contains("boom from a click") && text.Contains("InvalidOperationException") && text.Contains("at Tereditor.Checks.UiErrorChecks.Boom"), "The failing handler is logged with type, message and stack.");
            Require(notices.Count == 1 && notices[0].Details.Contains("boom from a click"), "One notice is raised.");
            Require(ran, "The dispatcher keeps processing jobs after the failure.");

            now += 1000; Click(); Dispatcher.UIThread.RunJobs(); guard.Flush();
            Require(notices.Count == 1 && File.ReadAllText(log).Split("boom from a click").Length - 1 >= 4, "A repeat within 10 s is logged but not noticed again.");
            now += 11_000; Click(); Dispatcher.UIThread.RunJobs();
            Require(notices.Count == 2, "After 10 s the same exception is noticed again.");
            window.Close();

            // Many different failures collapse into one "Many errors" notice.
            notices.Clear(); now += 120_000;
            Exception[] distinct = [new InvalidOperationException(), new ArgumentException(), new FormatException(), new IOException(), new NotSupportedException(), new TimeoutException(), new KeyNotFoundException(), new DivideByZeroException()];
            foreach (var failure in distinct) Require(guard.Handle(failure), "Distinct failures are handled.");
            Require(notices.Count(notice => !notice.Many) <= UiErrorGuard.NoticeBudget && notices.Count(notice => notice.Many) == 1, "The notice budget ends in one Many errors notice.");

            // A loop stops being handled.
            now += 120_000;
            var handled = 0;
            for (var index = 0; index < UiErrorGuard.LoopLimit + 10; index++)
            {
                try { Boom(); }
                catch (Exception error) { if (guard.Handle(error)) handled++; }
            }
            Require(handled == UiErrorGuard.LoopLimit, $"A looping exception is handled {UiErrorGuard.LoopLimit} times, then left to fail (handled {handled}).");

            Require(!guard.Handle(new OutOfMemoryException()) && !guard.Handle(new AccessViolationException()), "Out of memory and access violations are never handled.");
        }
        finally { guard.RemoveDispatcherHook(); }

        var starting = new UiErrorGuard(log, Path.Combine(directory, "crash.log"), () => now) { Notify = notices.Add };
        var before = notices.Count;
        Require(!starting.Handle(new InvalidOperationException("startup")) && notices.Count == before, "An exception before the main window exists is not handled and raises no notice.");
        Console.WriteLine("PASS UI error guard");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}