using System.Diagnostics;

namespace Tereditor.Plugins.Agent.Host;

/// <summary>What <c>--version</c> of an agent's program said.</summary>
internal sealed record AgentVersion(bool Installed, string? Version, string? Detail)
{
    /// <summary>
    /// Runs <c>&lt;executable&gt; --version</c> with a 10 s bound; the version is the last word it prints. <paramref name="name"/> names the
    /// agent in messages, and its lower-case form is the command word they quote. Never throws for a program that is missing or fails.
    /// </summary>
    public static async Task<AgentVersion> ProbeAsync(string executable, string name)
    {
        var command = name.ToLowerInvariant();
        static AgentVersion Missing(string detail) => new(false, null, detail);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return Missing($"{name} could not be started.");
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
            var errors = process.StandardError.ReadToEndAsync(cancel.Token);
            await process.WaitForExitAsync(cancel.Token);
            var text = (await output).Trim();
            if (process.ExitCode != 0) return Missing((await errors).Trim() is { Length: > 0 } detail ? detail : $"{command} --version exited {process.ExitCode}.");
            return new(true, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), null);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Missing($"{executable} was not found on PATH.");
        }
        catch (OperationCanceledException) { return Missing($"{command} --version timed out."); }
    }
}