using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>
/// The client methods an ACP agent calls: file reads and writes and terminals. The agent is untrusted, so every path and every
/// working directory is confined to the workspace root: it is resolved (symbolic links included) and refused when it leaves the root.
/// </summary>
/// <param name="root">The workspace root, the one root of every session unless <paramref name="rootsOf"/> says otherwise.</param>
/// <param name="unsaved">Gives the unsaved editor text of a file (its absolute path), or null; a read answers with it instead of the disk text.</param>
/// <param name="ask">Asks the user before a write to a sensitive file (see <see cref="AcpSensitivePaths"/>): the path, why, the old text and the new text. Without it such a write is refused.</param>
/// <param name="rootsOf">The roots of a session by its id, or null for a session this client does not know. Without it every session has the workspace root.</param>
internal sealed class AcpClientMethods(string root, Func<string, CancellationToken, Task<string?>>? unsaved = null, Func<string, string, string?, string, CancellationToken, Task<bool>>? ask = null, Func<string, IReadOnlyList<string>?>? rootsOf = null) : IAcpClientMethods, IDisposable
{
    public const int DefaultOutputLimit = 1_000_000;
    private const int MaxReadBytes = 8 * 1024 * 1024;

    private readonly string root = Resolve(Path.GetFullPath(root));
    private readonly ConcurrentDictionary<string, AcpTerminal> terminals = new();
    private int counter;

    /// <summary>Resolves symbolic links in the part of the path that exists, so a link inside the workspace cannot lead out of it.</summary>
    internal static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var existing = full;
        var tail = new Stack<string>();
        while (!Path.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
        {
            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }
        try
        {
            var info = new DirectoryInfo(existing);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) existing = target.FullName;
            else if (new FileInfo(existing).ResolveLinkTarget(true) is { } fileTarget) existing = fileTarget.FullName;
            // A link further up the chain: resolve the directory that holds it.
            if (Path.GetDirectoryName(existing) is { } holder && holder != existing && Resolve(holder) is var resolvedHolder && Path.GetFileName(existing) is { Length: > 0 } leaf)
                existing = Path.Combine(resolvedHolder, leaf);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        while (tail.Count > 0) existing = Path.Combine(existing, tail.Pop());
        return existing;
    }

    /// <summary>The absolute path inside the workspace, or an invalid-params error; a relative path is taken against the root.</summary>
    internal string Confine(string? path, IReadOnlyList<string>? roots = null)
    {
        if (string.IsNullOrEmpty(path)) throw new AcpException(AcpException.InvalidParams, "A path is required.");
        var resolved = Resolve(Path.IsPathRooted(path) ? path : Path.Combine(roots?[0] ?? root, path));
        if (!(roots ?? [root]).Any(candidate => Inside(candidate, resolved)))
            throw new AcpException(AcpException.InvalidParams, "That path is outside the workspace.");
        return resolved;
    }

    private static bool Inside(string folder, string resolved)
    {
        var relative = Path.GetRelativePath(folder, resolved);
        return !(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative));
    }

    /// <summary>The roots the call may touch: those of the session it names (checked now, at the time of use), else the workspace root.</summary>
    private IReadOnlyList<string> RootsOf(JsonObject parameters)
    {
        if (rootsOf is null || parameters["sessionId"] is not JsonValue value || !value.TryGetValue<string>(out var session)) return [root];
        return rootsOf(session)?.Select(folder => Resolve(Path.GetFullPath(folder))).ToArray() is { Length: > 0 } roots ? roots : throw new AcpException(AcpException.InvalidParams, "Unknown session.");
    }

    public async Task<JsonNode?> HandleAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "fs/read_text_file": return await ReadAsync(parameters, cancellationToken);
            case "fs/write_text_file":
                {
                    var roots = RootsOf(parameters);
                    var path = Confine(AcpFolder.Str(parameters, "path"), roots);
                    var content = AcpFolder.Str(parameters, "content");
                    var relative = Path.GetRelativePath(roots.First(candidate => Inside(candidate, path)), path);
                    if (AcpSensitivePaths.Reason(relative) is { } why)
                    {
                        var before = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
                        if (ask is null || !await ask(relative, why, before, content, cancellationToken))
                            throw new AcpException(AcpException.InvalidParams, $"The user did not allow a write to {relative}. {why}");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
                    return new JsonObject();
                }
            case "terminal/create": return Create(parameters);
            case "terminal/output":
                {
                    var terminal = Terminal(parameters);
                    return terminal.Snapshot();
                }
            case "terminal/wait_for_exit":
                {
                    var terminal = Terminal(parameters);
                    await terminal.Exited.WaitAsync(cancellationToken);
                    return terminal.ExitStatus();
                }
            case "terminal/kill":
                Terminal(parameters).Kill();
                return new JsonObject();
            case "terminal/release":
                {
                    var terminal = Terminal(parameters);
                    terminals.TryRemove(AcpFolder.Str(parameters, "terminalId"), out _);
                    terminal.Kill();
                    return new JsonObject();
                }
            default:
                throw new AcpException(AcpException.MethodNotFound, method);
        }
    }

    private async Task<JsonNode?> ReadAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var path = Confine(AcpFolder.Str(parameters, "path"), RootsOf(parameters));
        var info = new FileInfo(path);
        if (!info.Exists) throw new AcpException(-32002, "No such file: " + Path.GetRelativePath(root, path));
        if (info.Length > MaxReadBytes) throw new AcpException(AcpException.InvalidParams, "The file is larger than 8 MB.");
        // What the editor holds unsaved is what the user sees, so the agent reads that.
        var text = (unsaved is null ? null : await unsaved(path, cancellationToken)) ?? await File.ReadAllTextAsync(path, cancellationToken);
        var line = parameters["line"]?.GetValue<int>();
        var limit = parameters["limit"]?.GetValue<int>();
        if (line is not null || limit is not null)
        {
            var lines = text.Split('\n');
            text = string.Join('\n', lines.Skip(Math.Max(0, (line ?? 1) - 1)).Take(limit ?? int.MaxValue));
        }
        return new JsonObject { ["content"] = text };
    }

    private AcpTerminal Terminal(JsonObject parameters) =>
        terminals.TryGetValue(AcpFolder.Str(parameters, "terminalId"), out var terminal) ? terminal : throw new AcpException(AcpException.InvalidParams, "Unknown terminal.");

    private JsonNode Create(JsonObject parameters)
    {
        var cwd = parameters["cwd"] is JsonValue ? Confine(AcpFolder.Str(parameters, "cwd")) : root;
        var env = (parameters["env"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(variable => AcpFolder.Str(variable, "name"), variable => AcpFolder.Str(variable, "value"));
        var args = (parameters["args"] as JsonArray ?? []).Select(argument => argument?.GetValue<string>() ?? "").ToArray();
        var limit = parameters["outputByteLimit"]?.GetValue<int>() ?? DefaultOutputLimit;
        AcpTerminal terminal;
        try { terminal = AcpTerminal.Start(AcpFolder.Str(parameters, "command"), args, env, cwd, limit); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AcpException(AcpException.Internal, "Could not start the command: " + error.Message);
        }
        var id = "term" + Interlocked.Increment(ref counter);
        terminals[id] = terminal;
        return new JsonObject { ["terminalId"] = id };
    }

    public void Dispose()
    {
        foreach (var terminal in terminals.Values) terminal.Kill();
        terminals.Clear();
    }
}

/// <summary>One command an agent started: its merged output (cut at the agent's byte limit from the front) and its exit.</summary>
internal sealed class AcpTerminal
{
    private readonly Process process;
    private readonly StringBuilder output = new();
    private readonly int limit;
    private bool truncated;

    private AcpTerminal(Process process, int limit)
    {
        this.process = process;
        this.limit = limit;
        Exited = process.WaitForExitAsync();
    }

    public Task Exited { get; }

    public static AcpTerminal Start(string command, string[] args, IReadOnlyDictionary<string, string> env, string cwd, int limit)
    {
        var start = new ProcessStartInfo(command, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var (key, value) in env) start.Environment[key] = value;
        var process = Process.Start(start) ?? throw new InvalidOperationException("The command did not start.");
        process.StandardInput.Close();
        var terminal = new AcpTerminal(process, limit);
        process.OutputDataReceived += (_, data) => terminal.Append(data.Data);
        process.ErrorDataReceived += (_, data) => terminal.Append(data.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return terminal;
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (output)
        {
            output.Append(line).Append('\n');
            while (Encoding.UTF8.GetByteCount(output.ToString()) > limit && output.Length > 0)
            {
                output.Remove(0, Math.Max(1, output.Length / 8));
                truncated = true;
            }
        }
    }

    public JsonObject Snapshot()
    {
        // Let the readers drain what the process wrote before it exited.
        if (process.HasExited) process.WaitForExit();
        string text;
        bool cut;
        lock (output) { text = output.ToString(); cut = truncated; }
        var result = new JsonObject { ["output"] = text, ["truncated"] = cut };
        if (process.HasExited) result["exitStatus"] = ExitStatus();
        return result;
    }

    public JsonObject ExitStatus()
    {
        process.WaitForExit();
        return new JsonObject { ["exitCode"] = process.ExitCode, ["signal"] = null };
    }

    public void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}