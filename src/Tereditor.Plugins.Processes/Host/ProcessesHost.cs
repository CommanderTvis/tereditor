using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Processes.Host;

/// <summary>
/// The host half: the process snapshot and stop methods, and the agent-facing owned-process management that this plugin owns
/// (the report and release tools, the prompt section and the registry's switch). Disabled, none of it exists.
/// </summary>
public sealed class ProcessesHost : PluginHostModule
{
    public override PluginContract Contract => ProcessesContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        if (context.Processes is not { } processes) return ValueTask.FromResult<PluginDisposer?>(null);
        var sampler = new ProcessSampler(new HostSource(context, processes)) { StopAgentChildren = () => context.Settings<ProcessesSettings>().StopAgentChildren };
        // The embedded adapter runs a handler on the caller's thread, and sampling reads the process table.
        context.Method(ProcessesContract.Snapshot, async (_, _, token) =>
        {
            await processes.RefreshAsync(token);
            return await Task.Run(sampler.Sample, token);
        });
        context.Method(ProcessesContract.Stop, async (parameters, _, token) => new ProcessStopResult(await sampler.StopAsync(parameters.Id, token)));

        // A browser tab starts no process an agent could own, so the tools and their prompt would only mislead.
        if (!OperatingSystem.IsBrowser())
        {
            context.Tool(new PluginTool<OwnedProcessReportParams>(ProcessesPlugin.ReportTool, "Report a long-running process you started",
                "Report a long-running background process you started (a dev server, a watcher, a run_in_background command) so tereditor stops it when this terminal closes or tereditor quits, instead of leaving it running. Report only processes you started and want gone then; call owned_process_release if you stop it yourself. Refuses pid 1, tereditor, any terminal's shell, a dead pid and another user's process.",
                async (arguments, call, token) =>
                {
                    if (call.Terminal is not { } terminal) return new PluginToolResult("This session has no terminal tab to own a process.") { IsError = true };
                    try { return new PluginToolResult(await Task.Run(() => processes.ReportOwned(terminal, arguments.Pid, arguments.Description), token)); }
                    catch (ArgumentException error) { return new PluginToolResult(error.Message) { IsError = true }; }
                }));
            context.Tool(new PluginTool<OwnedProcessReleaseParams>(ProcessesPlugin.ReleaseTool, "Withdraw a reported process",
                "Tell tereditor a process you reported with owned_process_report is no longer yours to be stopped, usually because you stopped it. It does not stop the process.",
                (arguments, call, _) => ValueTask.FromResult(call.Terminal is not { } terminal
                    ? new PluginToolResult("This session has no terminal tab to own a process.") { IsError = true }
                    : new PluginToolResult(processes.ReleaseOwned(terminal, arguments.Pid) ? $"Released pid {arguments.Pid}." : $"This terminal had not reported pid {arguments.Pid}; nothing changed."))));
            // Read-only: the guard's own state and events, never a process listing; the report names terminals tereditor started and no command lines.
            context.Tool(new PluginTool<ProcessesStatusParams>(ProcessesPlugin.StatusTool, "Why a terminal program was stopped",
                "Read-only. Answers, as compact JSON, whether tereditor's terminal memory guard is on and its limits (per terminal, total, grace seconds, protected programs, physical memory), each terminal of this host with its memory against its limit, the guard state (Ok, Warned, Terminating, Killing) and its largest program, and the guard's recent events (kind, time, terminal, text; newest last, up to 50; kept in ~/.tereditor/logs/memory-guard.log, so they survive a restart and a closed terminal). Call it when a build, test run or server in a terminal died with exit code 143 (SIGTERM) or 137 (SIGKILL), or csc or dotnet vanished: a Stopped or Killed event naming the program, its pid and size, in your workspace and near that time, means the guard ended it. Lists only terminals tereditor started; no command lines, no environment, no other processes.",
                async (_, call, token) => new PluginToolResult(await Task.Run(() => processes.MemoryGuardReport(call.WorkspaceId), token))));
            context.Prompt(new PromptContribution
            {
                Id = "owned-processes",
                Title = "Owned processes",
                Description = "Report long-running background processes with owned_process_report, release them with owned_process_release.",
                Placement = PromptPlacement.Section,
                Text = """
                    Report every long-running background process you start (dev servers, watchers, `run_in_background` commands) with `owned_process_report` (its `pid`, and a short `description`), so tereditor stops it when the terminal closes or tereditor quits.
                    Call `owned_process_release` when you stop it yourself. Report only processes you started.
                    """
            });
        }

        processes.ManageOwned(true);
        // The guard reads the settings at every sample, so the switch and the limits apply at once; disabling the plugin withdraws it.
        processes.ManageMemoryGuard(() => context.Settings<ProcessesSettings>() is { MemoryGuard: true } settings
            ? (settings.PerTerminalMb, settings.TotalMb, settings.GraceSeconds, settings.Protect) : null);
        // Disabling forgets the records without stopping the processes: the user opted out of having them managed.
        return ValueTask.FromResult<PluginDisposer?>(() =>
        {
            processes.ManageMemoryGuard(null);
            processes.ManageOwned(false);
            return ValueTask.CompletedTask;
        });
    }

    private sealed class HostSource(IPluginHostContext context, IProcessHost processes) : IProcessSource
    {
        public ProcessInputs Read()
        {
            var shells = context.Terminals();
            var perWorkspace = new Dictionary<string, int>(StringComparer.Ordinal);
            var terminals = shells.Select(shell =>
            {
                var index = perWorkspace[shell.Terminal.WorkspaceId] = perWorkspace.GetValueOrDefault(shell.Terminal.WorkspaceId) + 1;
                return new TerminalInfo(shell.Terminal, shell.Pid, context.AgentRecord(shell.Terminal)?.Kind, $"tab {index}", context.TerminalTitle(shell.Terminal), MemoryText(shell.Terminal));
            }).Concat(context.ListAgentSessions().Select(session => new TerminalInfo(new(session.WorkspaceId, "chat:" + session.Id), session.Pid, session.Agent, "chat", session.Title))).ToArray();
            return new(terminals, processes.Table(), processes.LanguageServers(), processes.OwnedProcesses(), processes.LanguageServerName)
            {
                Workers = OperatingSystem.IsBrowser(),
                GuardEvents = [.. processes.MemoryGuardEvents().Select(item => (item.At, item.Text))]
            };
        }

        // "memory 2.1 of 8 GB", with the guard's state when it is not Ok: how the terminal's tree stands against its limit.
        private string? MemoryText(TerminalRef terminal)
        {
            if (processes.TerminalMemory(terminal) is not { } memory) return null;
            static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString(bytes >= 10L << 30 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
            return $"memory {Gb(memory.Bytes)} of {Gb(memory.Limit)}\u00A0GB" + (memory.State == "Ok" ? "" : " (" + memory.State.ToLowerInvariant() + ")");
        }

        public (TimeSpan Cpu, long MemoryBytes)? Usage(int pid) => processes.Usage(pid);

        public string WorkspaceLabel(string workspace)
        {
            var trimmed = workspace.TrimEnd('/', '\\');
            var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
            return name.Length > 0 ? name : workspace;
        }

        public ValueTask StopLanguageServerAsync(string workspace, string serverId, CancellationToken cancellationToken) => processes.StopLanguageServerAsync(workspace, serverId, cancellationToken);

        public ValueTask StopProcessAsync(int pid, string command, CancellationToken cancellationToken) => processes.StopProcessAsync(pid, command, cancellationToken);
    }
}