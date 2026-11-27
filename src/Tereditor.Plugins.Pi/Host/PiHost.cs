using System.Text.Json.Serialization.Metadata;

using Tereditor.Plugins.Agent.Host;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>The Pi plugin's host half: the version probe and the chat sessions over <c>pi --mode rpc</c>.</summary>
public sealed class PiHost : PluginHostModule
{
    public override PluginContract Contract => PiContract.Contract;

    public override IJsonTypeInfoResolver Json => PiHostJson.Default;

    /// <summary>The status of the Pi the settings choose, with the state of the pinned download added.</summary>
    internal static async Task<PiStatus> ReadStatusAsync(PiSettings settings, PiManaged managed, IReadOnlyList<AgentLaunchContribution>? contributions = null) =>
        settings.Source == PiSources.Managed && PiLinks.InTab is null
            ? await Task.Run(() => managed.Status(PiSources.Managed))
            : managed.Status(PiSources.System, await ReadStatusAsync(settings.Command));

    internal static async Task<PiStatus> ReadStatusAsync(string command)
    {
        if (PiLinks.InTab is not null) return new PiStatus(true, "in this tab") { Version = PiLinks.InTabVersion is { } version ? await version() : null };
        var executable = command.Trim() is { Length: > 0 } trimmed ? trimmed : "pi";
        var probe = await AgentVersion.ProbeAsync(executable, "Pi");
        return new PiStatus(probe.Installed, executable) { Version = probe.Version, Detail = probe.Detail };
    }

    private static string HomeDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // The Settings › Pi pane's read: the files, the settings, and the open chat's commands.
    private static async ValueTask<PiConfigSnapshot> ConfigAsync(IPluginHostContext context, PiChatHost chats, string workspaceId, CancellationToken cancellationToken)
    {
        var cwd = (await context.WorkspaceAsync(workspaceId, cancellationToken))?.Path ?? workspaceId;
        var header = chats.HeaderOf(workspaceId);
        var agent = PiConfigReader.AgentDir(PiChatHost.ContributedEnvironment(context), Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR"), HomeDirectory());
        var commands = header?.Commands.Select(command => new PiConfigCommand(command.Name, command.Source ?? "", command.Description ?? "")).ToArray() ?? [];
        var snapshot = await Task.Run(() => PiConfigReader.Read(new PiConfigRoots(agent, HomeDirectory(), cwd), commands, header is not null), cancellationToken);
        // A running chat carries tereditor's own server through a per-run extension; show it beside the files' servers.
        if (header is not null && PiLinks.InTab is null)
            snapshot = snapshot with { Items = [.. snapshot.Items, new PiConfigItem(PiConfigKinds.Mcp, PiScopes.Tereditor, PiMcpExtension.ServerName, Path.Combine(PiMcpExtension.DefaultDirectory(), "tereditor-mcp.mjs"), true, "Added by tereditor for this chat only; nothing is written to mcp.json.")] };
        return snapshot with { Provider = AgentLaunchComposition.ProviderName(context.AgentLaunchContributions(PiManifest.Id)) ?? "Pi's own provider (built in)" };
    }

    // `pi install` or `pi remove`, started the way a chat starts Pi: the chosen Pi, the managed copy on Bun, or a model source's wrapper.
    private static async ValueTask<PiPackageResult> PackageAsync(IPluginHostContext context, PiManaged managed, string verb, PiPackageParams request, CancellationToken cancellationToken)
    {
        if (!PiPackages.ValidSource(request.Source)) return new(false, "Enter a package source, such as npm:@scope/package.");
        if (PiLinks.InTab is not null) return new(false, "Packages can be changed only from a Pi that runs on this computer.");
        var settings = context.Settings<PiSettings>();
        var cwd = (await context.WorkspaceAsync(request.WorkspaceId, cancellationToken))?.Path ?? request.WorkspaceId;
        var command = settings.Command.Trim() is { Length: > 0 } own ? own : "pi";
        var all = context.AgentLaunchContributions(PiManifest.Id);
        var pinned = settings.Source == PiSources.Managed;
        var contributions = pinned || Path.GetFileNameWithoutExtension(command) == "pi" ? all : [];
        var replacing = contributions.FirstOrDefault(contribution => contribution.ReplacesExecutable && contribution.Wrapper.Count > 0)?.Wrapper ?? [];
        var spawn = new PiSpawn(command, cwd)
        {
            Wrapper = replacing,
            Blocked = AgentLaunchComposition.Blocked(contributions) ?? (pinned ? PiChatHost.ManagedBlock(contributions) : null),
            Environment = PiChatHost.ContributedEnvironment(context)
        };
        if (pinned)
        {
            try { spawn = await managed.PrepareAsync(spawn, context.TrackRuntimeProcess); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                return new(false, managed.LastError is { Length: > 0 } failure ? failure : error.Message);
            }
        }
        return await PiPackages.RunAsync(spawn, PiPackages.Arguments(verb, request.Source, request.Local), cancellationToken);
    }

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        // A launch uses Bun only when it is already there; only the Download button may fetch Pi, and with it Bun.
        async Task<RuntimeResolution> FindBunAsync(string workspace, CancellationToken token)
        {
            var present = (await context.RuntimeStatusAsync(token)).Any(status => status.Kind == RuntimeKind.Bun && status.Source is not null);
            return present ? await context.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, token) : RuntimeResolution.Unavailable(RuntimeKind.Bun, PiManaged.NoBun);
        }
        var managed = new PiManaged(PiManaged.DefaultRoot(), (workspace, token) => context.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, token).AsTask(), FindBunAsync);
        var chats = new PiChatHost(context, managed: managed);
        async Task<PiStatus> StatusAsync() => (await ReadStatusAsync(context.Settings<PiSettings>(), managed, context.AgentLaunchContributions(PiManifest.Id))) with { Models = chats.ReportedModels() };
        context.Method(PiContract.Status, async (_, _, _) => await Task.Run(() => StatusAsync()));
        context.Method(PiContract.Config, (request, _, token) => ConfigAsync(context, chats, request.WorkspaceId, token));
        context.Method(PiContract.PackageInstall, (request, _, token) => PackageAsync(context, managed, "install", request, token));
        context.Method(PiContract.PackageRemove, (request, _, token) => PackageAsync(context, managed, "remove", request, token));
        ManagedDownloads.Register(context, managed, PiContract.Install, PiContract.CancelInstall, PiContract.Remove, token => Task.Run(StatusAsync, token));
        chats.Register();
        return ValueTask.FromResult<PluginDisposer?>(() => new ValueTask(chats.DisposeAsync()));
    }
}