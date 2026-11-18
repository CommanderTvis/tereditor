using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

using Avalonia.Threading;

namespace Tereditor.UI;

/// <summary>What the app shows for a guarded failure: the area it happened in and the text the log holds.</summary>
internal sealed record UiErrorNotice(string Area, string Details, bool Many = false);

/// <summary>
/// The layered guard against a failing UI handler ending the app. An exception that reaches the UI dispatcher is logged to
/// <c>~/.tereditor/logs/ui-errors.log</c> (about 2 MB, one rotated copy <c>.1</c>; a background thread writes, the UI thread only enqueues)
/// and answered with one toast, and the app keeps running. The same exception type and stack notifies at most once per 10 s, and more than
/// 5 notices in a minute collapse into one "Many errors" notice. Never handled, so the process still fails loudly: out of memory,
/// access violations and other corrupted state, stack exhaustion, exceptions raised before the main window exists, and an exception that
/// repeats more than <see cref="LoopLimit"/> times within <see cref="LoopWindow"/> (a broken frame or timer loop). Off the UI thread an
/// unhandled exception is written to <c>crash.log</c> synchronously before the process dies, and an unobserved task exception is logged.
/// Log lines carry type, message, stack and the active window, never document contents.
/// </summary>
internal sealed class UiErrorGuard
{
    internal const int LoopLimit = 25;
    internal static readonly TimeSpan LoopWindow = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan NoticeInterval = TimeSpan.FromSeconds(10);
    internal const int NoticeBudget = 5;
    internal static readonly TimeSpan BudgetWindow = TimeSpan.FromSeconds(60);
    private const long Limit = 2 * 1024 * 1024;

    private readonly string logPath, crashPath;
    private readonly Func<long> clock;
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Dictionary<string, Queue<long>> recent = [];
    private readonly Dictionary<string, long> notified = [];
    private readonly Queue<long> notices = new();
    private readonly object gate = new();
    private Thread? writer;
    private long enqueued, written;
    private bool manyShown;

    internal UiErrorGuard(string logPath, string crashPath, Func<long>? clock = null)
    {
        this.logPath = logPath;
        this.crashPath = crashPath;
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    private static string LogsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "logs");

    /// <summary>The app's guard.</summary>
    internal static UiErrorGuard Shared { get; } = new(Path.Combine(LogsDirectory, "ui-errors.log"), Path.Combine(LogsDirectory, "crash.log"));

    /// <summary>True once the main window exists; an exception before that is never handled.</summary>
    internal bool Running { get; set; }

    /// <summary>Shows a notice; set by the app to its active window's toasts. Called on the UI thread.</summary>
    internal Action<UiErrorNotice>? Notify { get; set; }

    internal string LogPath => logPath;

    /// <summary>Process-wide hooks that work off the UI thread too; call once, as early as possible.</summary>
    internal void InstallProcessHooks()
    {
        if (OperatingSystem.IsBrowser()) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception, e.ExceptionObject?.ToString() ?? "");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("unobserved-task", e.Exception, null); e.SetObserved(); };
    }

    /// <summary>Hooks the UI dispatcher; call on the UI thread once the application exists.</summary>
    internal void InstallDispatcherHook() => Dispatcher.UIThread.UnhandledException += OnUnhandled;

    internal void RemoveDispatcherHook() => Dispatcher.UIThread.UnhandledException -= OnUnhandled;

    private void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e) => e.Handled = Handle(e.Exception);

    /// <summary>Logs the exception and notifies; returns true when the app may keep running.</summary>
    internal bool Handle(Exception error, string? plugin = null)
    {
        var root = Unwrap(error);
        var refused = !Running || Corrupt(root);
        var key = root.GetType().FullName + "|" + root.StackTrace;
        var now = clock();
        var looping = false;
        var notice = false;
        var many = false;
        lock (gate)
        {
            if (!recent.TryGetValue(key, out var times)) recent[key] = times = new();
            times.Enqueue(now);
            while (times.Count > 0 && now - times.Peek() > LoopWindow.TotalMilliseconds) times.Dequeue();
            looping = times.Count > LoopLimit;
            if (!refused && !looping)
            {
                if (!notified.TryGetValue(key, out var last) || now - last >= NoticeInterval.TotalMilliseconds)
                {
                    notified[key] = now;
                    while (notices.Count > 0 && now - notices.Peek() > BudgetWindow.TotalMilliseconds) notices.Dequeue();
                    if (notices.Count < NoticeBudget) { notices.Enqueue(now); notice = true; manyShown = false; }
                    else if (!manyShown) { manyShown = many = true; }
                }
            }
            if (recent.Count > 200) { recent.Clear(); notified.Clear(); }
        }
        Log(refused ? "refused" : looping ? "loop" : "handled", error, plugin);
        if (refused || looping) return false;
        if (notice) Show(new(Area(root, plugin), Describe(error)));
        else if (many) Show(new("many places", "Many errors happened. See the log.", true));
        return true;
    }

    private void Show(UiErrorNotice notice)
    {
        try { Notify?.Invoke(notice); }
        catch (Exception failure) { Log("notice-failed", failure, null); }
    }

    /// <summary>Reports a failure of a plugin's own callback, naming the plugin; never throws.</summary>
    internal void Plugin(string pluginId, Exception error) => Handle(error, pluginId);

    // The exceptions that mean the process state is not trustworthy.
    private static bool Corrupt(Exception error) => error is OutOfMemoryException or AccessViolationException or InsufficientExecutionStackException
        or System.Runtime.InteropServices.SEHException or AppDomainUnloadedException or BadImageFormatException;

    private static Exception Unwrap(Exception error)
    {
        while (error is AggregateException { InnerExceptions.Count: 1 } or System.Reflection.TargetInvocationException && error.InnerException is { } inner) error = inner;
        return error;
    }

    // "the Claude Code plugin" when the stack starts in a plugin assembly, else the first Tereditor type on it.
    internal static string Area(Exception error, string? plugin)
    {
        if (plugin is not null) return plugin + " plugin";
        foreach (var line in (error.StackTrace ?? "").Split('\n'))
        {
            var at = line.IndexOf("at Tereditor.", StringComparison.Ordinal);
            if (at < 0) continue;
            var name = line[(at + 3)..];
            name = name[..Math.Max(0, name.IndexOf('(') is var paren and >= 0 ? paren : name.Length)];
            var parts = name.Split('.');
            if (parts is ["Tereditor", "Plugins", { } id, ..]) return id + " plugin";
            return parts.Length >= 2 ? parts[^2] : "the app";
        }
        return "the app";
    }

    private static string Describe(Exception error) =>
        $"{error.GetType().FullName}: {error.Message}{Environment.NewLine}{error.StackTrace}";

    private void Log(string outcome, Exception error, string? plugin)
    {
        if (OperatingSystem.IsBrowser()) return;
        var window = "";
        try { window = ActiveWindow?.Invoke() ?? ""; } catch (Exception) { }
        var text = new StringBuilder()
            .Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append(' ').Append(outcome)
            .Append(" thread=").Append(Environment.CurrentManagedThreadId).Append(" window=").Append(window.ReplaceLineEndings(" "))
            .Append(" plugin=").Append(plugin ?? "").AppendLine().Append(error).AppendLine().ToString();
        EnsureWriter();
        Interlocked.Increment(ref enqueued);
        queue.Writer.TryWrite(text);
    }

    /// <summary>Names the active window for the log; set by the app and run on the UI thread, where handlers run.</summary>
    internal Func<string?>? ActiveWindow { get; set; }

    /// <summary>Waits until every queued line is on disk (checks).</summary>
    internal void Flush()
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Volatile.Read(ref written) < Volatile.Read(ref enqueued) && Environment.TickCount64 < deadline) Thread.Sleep(5);
    }

    private void Crash(Exception? error, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
            Rotate(crashPath);
            File.AppendAllText(crashPath, $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} crash thread={Environment.CurrentManagedThreadId}{Environment.NewLine}{(error is null ? text : error.ToString())}{Environment.NewLine}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    private void EnsureWriter()
    {
        if (writer is not null) return;
        lock (gate)
        {
            if (writer is not null) return;
            writer = new Thread(Drain) { IsBackground = true, Name = "ui-errors-log" };
            writer.Start();
        }
    }

    private void Drain()
    {
        var reader = queue.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            var count = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                Rotate(logPath);
                using var open = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                while (reader.TryRead(out var text)) { open.Write(text); count++; }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down; drop what could not be written.
                while (reader.TryRead(out _)) count++;
            }
            Interlocked.Add(ref written, count);
        }
    }

    private static void Rotate(string target)
    {
        try
        {
            var file = new FileInfo(target);
            if (file.Exists && file.Length > Limit) File.Move(target, target + ".1", true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}