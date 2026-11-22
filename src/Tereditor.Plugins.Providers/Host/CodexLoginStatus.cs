using System.Diagnostics;

namespace Tereditor.Plugins.Providers.Host;

/// <summary>
/// Reads whether Codex is signed in by running <c>codex login status</c> on the host. Only the status comes out: the output is looked at for
/// the kind of sign-in and dropped, so no token (an API key shows masked there) is kept, logged or sent.
/// </summary>
internal static class CodexLoginStatus
{
    public static async Task<ProviderSignIn> ReadAsync(string command, CancellationToken cancel = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, ["login", "status"]) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return new(false);
            process.StandardInput.Close();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(limit.Token);
                var errors = process.StandardError.ReadToEndAsync(limit.Token);
                await process.WaitForExitAsync(limit.Token);
                var text = await output + await errors;
                if (process.ExitCode != 0 || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)) return new(false);
                return new(true, text.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "ChatGPT" : text.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "API key" : null);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (cancel.IsCancellationRequested) throw;
                return new(false);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return new(false); }
    }
}