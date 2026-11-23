using System.Diagnostics;
using System.Text;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>
/// `pi install` and `pi remove` for the Settings › Pi pane. They run the Pi a chat would run (the same command words, environment and
/// working directory), with stdin closed so Pi never waits for an answer. Nothing is read from Pi's output but the last lines for a message.
/// </summary>
internal static class PiPackages
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>A source is one argument: no blanks, no control characters, and not a flag.</summary>
    public static bool ValidSource(string source) =>
        source.Length is > 0 and <= 300 && source[0] != '-' && !source.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    /// <summary>The arguments after the Pi words: `install &lt;source&gt;`, with `-l` for the project's settings.</summary>
    public static IReadOnlyList<string> Arguments(string verb, string source, bool local) =>
        local ? [verb, source, "-l"] : [verb, source];

    public static async Task<PiPackageResult> RunAsync(PiSpawn spawn, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (spawn.Blocked is { } reason) return new(false, reason);
        // Same words as a chat's child: a wrapper's remaining words and a Bun entry point come before Pi's own arguments.
        var words = spawn.Wrapper.Skip(1).Concat(spawn.Prefix).Concat(arguments).ToList();
        var start = new ProcessStartInfo(spawn.Executable, words)
        {
            WorkingDirectory = spawn.Cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var (name, value) in spawn.Environment) start.Environment[name] = value;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Pi could not be started.");
            spawn.Started?.Invoke(process.Id);
            process.StandardInput.Close();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Limit);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new(false, "Pi ran past five minutes and was stopped.");
            }
            var text = Tail((await output) + "\n" + (await errors));
            return new(process.ExitCode == 0, text.Length > 0 ? text : process.ExitCode == 0 ? "Done." : $"Pi exited {process.ExitCode}.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return new(false, error.Message);
        }
    }

    // The last lines of the output, so a long npm log does not reach the pane whole.
    internal static string Tail(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToArray();
        return string.Join("\n", lines.TakeLast(12));
    }
}