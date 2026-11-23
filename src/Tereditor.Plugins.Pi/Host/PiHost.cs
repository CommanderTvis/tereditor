using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>The Pi plugin's host half: the version probe and the chat sessions over <c>pi --mode rpc</c>.</summary>
public sealed class PiHost : PluginHostModule
{
    public override PluginContract Contract => PiContract.Contract;

    public override IJsonTypeInfoResolver Json => PiHostJson.Default;

    /// <summary>The status of the Pi the settings choose, with the state of the pinned download added.</summary>
    internal static async Task<PiStatus> ReadStatusAsync(PiSettings settings, PiManaged managed) =>
        settings.Source == PiSources.Managed && PiLinks.InTab is null
            ? await Task.Run(() => managed.Status(PiSources.Managed))
            : managed.Status(PiSources.System, await ReadStatusAsync(settings.Command));

    internal static async Task<PiStatus> ReadStatusAsync(string command)
    {
        if (PiLinks.InTab is not null) return new PiStatus(true, "in this tab") { Version = PiLinks.InTabVersion is { } version ? await version() : null };
        var executable = command.Trim() is { Length: > 0 } trimmed ? trimmed : "pi";
        var status = new PiStatus(false, executable);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return status with { Detail = "Pi could not be started." };
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
            var errors = process.StandardError.ReadToEndAsync(cancel.Token);
            await process.WaitForExitAsync(cancel.Token);
            var text = (await output).Trim();
            if (process.ExitCode != 0) return status with { Detail = (await errors).Trim() is { Length: > 0 } detail ? detail : $"pi --version exited {process.ExitCode}." };
            return status with { Installed = true, Version = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() };
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return status with { Detail = $"{executable} was not found on PATH." };
        }
        catch (OperationCanceledException) { return status with { Detail = "pi --version timed out." }; }
    }

    private static string HomeDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // The Settings › Pi pane's read: the files, the settings, and the open chat's commands.
    private static async ValueTask<PiConfigSnapshot> ConfigAsync(IPluginHostContext context, PiChatHost chats, string workspaceId, CancellationToken cancellationToken)
    {
        var cwd = (await context.WorkspaceAsync(workspaceId, cancellationToken))?.Path ?? workspaceId;
        var header = chats.HeaderOf(workspaceId);
        var agent = PiConfigReader.AgentDir(PiChatHost.ContributedEnvironment(context), Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR"), HomeDirectory());
        var commands = header?.Commands.Select(command => new PiConfigCommand(command.Name, command.Source ?? "", command.Description ?? "")).ToArray() ?? [];
        return await Task.Run(() => PiConfigReader.Read(new PiConfigRoots(agent, HomeDirectory(), cwd), commands, header is not null), cancellationToken);
    }

    // `pi install` or `pi remove`, started the way a chat starts Pi: the chosen Pi, the managed copy on Bun, or a model source's wrapper.
    private static async ValueTask<PiPackageResult> PackageAsync(IPluginHostContext context, PiManaged managed, string verb, PiPackageParams request, CancellationToken cancellationToken)
    {
        if (!PiPackages.ValidSource(request.Source)) return new(false, "Enter a package source, such as npm:@scope/package.");
        if (PiLinks.InTab is not null) return new(false, "Packages can be changed only from a Pi that runs on this computer.");
        var settings = context.Settings<PiSettings>();
        var cwd = (await context.WorkspaceAsync(request.WorkspaceId, cancellationToken))?.Path ?? request.WorkspaceId;
        var command = settings.Command.Trim() is { Length: > 0 } own ? own : "pi";
        var pinned = settings.Source == PiSources.Managed;
        var contributions = pinned || Path.GetFileNameWithoutExtension(command) == "pi" ? context.AgentLaunchContributions(PiManifest.Id) : [];
        var replacing = contributions.FirstOrDefault(contribution => contribution.ReplacesExecutable && contribution.Wrapper.Count > 0)?.Wrapper ?? [];
        var spawn = new PiSpawn(command, cwd)
        {
            Wrapper = pinned ? [] : replacing,
            Blocked = AgentLaunchComposition.Blocked(contributions) ?? (pinned && replacing.Count > 0 ? "This Model source starts the system Pi. Set Pi source to System command, or choose another Model source." : null),
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
        var managed = new PiManaged(PiManaged.DefaultRoot(), (workspace, token) => context.ResolveRuntimeAsync(RuntimeKind.Bun, workspace, token).AsTask());
        var chats = new PiChatHost(context, managed: managed);
        context.Method(PiContract.Status, async (_, _, _) => await Task.Run(() => ReadStatusAsync(context.Settings<PiSettings>(), managed)));
        context.Method(PiContract.Config, (request, _, token) => ConfigAsync(context, chats, request.WorkspaceId, token));
        context.Method(PiContract.PackageInstall, (request, _, token) => PackageAsync(context, managed, "install", request, token));
        context.Method(PiContract.PackageRemove, (request, _, token) => PackageAsync(context, managed, "remove", request, token));
        context.Method(PiContract.Install, async (_, _, token) =>
        {
            try { await managed.EnsureAsync("", token); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException) { context.Log.Debug("pi download: " + error.Message); }
            return await Task.Run(() => ReadStatusAsync(context.Settings<PiSettings>(), managed), CancellationToken.None);
        });
        context.Method(PiContract.CancelInstall, async (_, _, _) =>
        {
            managed.Cancel();
            return await Task.Run(() => ReadStatusAsync(context.Settings<PiSettings>(), managed));
        });
        context.Method(PiContract.Remove, async (_, _, _) =>
        {
            await Task.Run(managed.Remove);
            return await Task.Run(() => ReadStatusAsync(context.Settings<PiSettings>(), managed));
        });
        // Turning the plugin off deletes what it downloaded.
        context.OnUserDisabled(() =>
        {
            managed.Remove();
            return ValueTask.CompletedTask;
        });
        chats.Register();
        return ValueTask.FromResult<PluginDisposer?>(() => new ValueTask(chats.DisposeAsync()));
    }
}