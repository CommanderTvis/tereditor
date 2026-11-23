using Tereditor.Host.Abstractions;

namespace Tereditor.Host.Core.Language;

internal sealed partial class LanguageSession
{
    private const int ImportLogBytes = 128 * 1024;
    private string importFailure = "";
    private string importFileKey = "";
    private string importFileText = "";
    private long importRefreshed;
    private int importRefreshing;

    /// <summary>The failure the last read of the server's log found, or empty; reads no file, so listings may use it.</summary>
    internal string ImportFailureText => importFailure;

    /// <summary>
    /// The import state: <c>failed</c> once the server's log says the Gradle or Maven import failed (until the session ends), <c>importing</c>
    /// while the progress line names an import, else empty; the text is the first error line, or the progress line.
    /// </summary>
    internal async Task<(string State, string Text)> ImportAsync(CancellationToken token)
    {
        if (importFailure.Length == 0)
        {
            var files = ownedCache is { Length: > 0 } directory ? server.Spec.LogFiles(directory) : [];
            if (files.Count > 0)
            {
                var key = string.Join("|", files.Select(file => file + ":" + (File.Exists(file) ? new FileInfo(file).Length + ":" + File.GetLastWriteTimeUtc(file).Ticks : "")));
                if (key != importFileKey) { importFileText = await ReadLogTailAsync(files, token); importFileKey = key; }
            }
            if (server.Spec.ImportFailure(importFileText + "\n" + log.ServerTail(StallLogLines * 10)) is { Length: > 0 } found)
            {
                importFailure = found;
                log.Add("import", "Project import failed: " + found);
            }
        }
        if (importFailure.Length > 0) return ("failed", importFailure);
        return IsIndexing && IsImportProgress(detail) ? ("importing", detail) : ("", "");
    }

    /// <summary>Refreshes <see cref="ImportFailureText"/> on a pool thread, at most every 5 s, for callers that may not touch files.</summary>
    internal void RefreshImportSoon()
    {
        if (importFailure.Length > 0 || Environment.TickCount64 - Interlocked.Read(ref importRefreshed) < 5_000 || Interlocked.Exchange(ref importRefreshing, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await ImportAsync(lifetime.Token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
            finally { Interlocked.Exchange(ref importRefreshed, Environment.TickCount64); Interlocked.Exchange(ref importRefreshing, 0); }
        });
    }

    internal static bool IsImportProgress(string progress) =>
        progress.Contains("import", StringComparison.OrdinalIgnoreCase) || progress.Contains("Gradle", StringComparison.OrdinalIgnoreCase) || progress.Contains("Maven", StringComparison.OrdinalIgnoreCase);

    /// <summary>The last 128 KiB of the first log file that exists, shared with the server that still writes it.</summary>
    internal static async Task<string> ReadLogTailAsync(IEnumerable<string> files, CancellationToken token)
    {
        foreach (var file in files)
        {
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
                if (stream.Length > ImportLogBytes) stream.Seek(-ImportLogBytes, SeekOrigin.End);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                return await reader.ReadToEndAsync(token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return "";
    }

    /// <summary>What the answer says about the import, ahead of its own detail; empty when there is nothing to say.</summary>
    internal static string ImportNote(string state, string text) => state switch
    {
        "failed" => "Project import failed, so this answer can be empty or wrong even though the server runs: " + text + " Read the log with lsp_status operation=logs.",
        "importing" => "The project import is still running (" + text + "), so project-wide answers are incomplete.",
        _ => ""
    };

    /// <summary>Marks <paramref name="result"/> with the import state: a failed import is never <c>current</c>.</summary>
    internal static LanguageResult WithImport(LanguageResult result, string state, string text)
    {
        if (state.Length == 0) return result;
        var note = ImportNote(state, text);
        return state == "failed"
            ? result with { ImportState = state, ImportError = text, Detail = note + (result.Detail.Length > 0 ? " " + result.Detail : ""), Freshness = result.Freshness == "current" ? "partial" : result.Freshness, Coverage = "Project import failed: no project-wide result is reliable. " + result.Coverage }
            : result with { ImportState = state, ImportError = text };
    }

    /// <summary>The coverage sentence of a status: import failed, import running, or the index still building.</summary>
    internal string ImportCoverage((string State, string Text) import) => import.State switch
    {
        "failed" => " Project import FAILED: " + import.Text,
        "importing" => " The project import is running; project-wide results are incomplete until it ends.",
        _ => IsIndexing ? " The project import is done; the index is still building, so project-wide results are incomplete." : ""
    };
}