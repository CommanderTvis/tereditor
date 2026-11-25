using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Omp.UI;

/// <summary>
/// The omp plugin's UI half: Settings › omp, the side tool, the agent launcher and its start action, and the tab
/// icon of terminals running omp.
/// </summary>
public sealed class OmpUI : PluginUIModule
{
    /// <summary>The launch line with this host's settings; every new launch gets its own session folder.</summary>
    public static string LaunchLine(IPluginUIContext context, LauncherCommandOptions options, OmpStatus? status = null)
    {
        var settings = context.Settings<OmpSettings>();
        var resume = options.ResumeSessionId;
        // The managed omp starts through the program the host wrote for it, which keeps the word "omp" other plugins' launch contributions look for.
        var executable = status is { Source: OmpSources.Managed, LaunchCommand: { Length: > 0 } program } ? OmpLaunch.ManagedExecutable(program) : settings.Command;
        return OmpLaunch.Line(executable, new()
        {
            Mcp = settings.Mcp,
            Windows = context.Host().HostPlatform == HostPlatform.Windows,
            UiLaunch = true,
            Model = options.Model is { Length: > 0 } model ? model : settings.Model,
            ApprovalMode = settings.ApprovalMode,
            Advisor = settings.Advisor,
            OwnCodeIntelligence = settings.OwnCodeIntelligence,
            SessionId = resume ?? OmpLaunch.NewSessionId(),
            Continue = resume is not null,
            SystemPrompt = options.SystemPrompt,
            InitialPrompt = options.InitialPrompt,
            Images = options.Images,
            Contributions = context.AgentLaunchContributions(OmpManifest.Id)
        });
    }

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        OmpStatus? status = null;
        OmpGlyph.Reset();
        // The state is read on the UI thread, where launches and the availability read it.
        void Apply(OmpStatus read)
        {
            if (Dispatcher.UIThread.CheckAccess()) { status = read; context.Invalidate(); }
            else Dispatcher.UIThread.Post(() => { status = read; context.Invalidate(); });
        }
        async Task<OmpStatus?> Probe()
        {
            try
            {
                var read = await context.RequestAsync(OmpContract.Status, new OmpNoParams());
                Apply(read);
                return read;
            }
            catch (Exception) { return null; }
        }
        // A managed omp that is not downloaded yet is never downloaded by a start: the start is skipped and the toast points to the Download button.
        async Task<OmpStatus?> Prepare()
        {
            var current = await Probe();
            if (current is { Source: OmpSources.Managed, Installed: false })
            {
                context.Notify(PluginNotificationKind.Error, "omp is not downloaded yet. Open Settings, choose omp and press Download.");
                return null;
            }
            return current;
        }
        context.SettingsSection(new("omp", OmpManifest.Icon, () => new OmpSettingsView(context, () => status, Apply)));
        // The source, the command and the profile all change what a launch runs, so the state is read again.
        var settingsWatch = context.OnSettings<OmpSettings>(changed => _ = Probe());
        context.AgentPane(new(OmpManifest.Id, "omp", OmpManifest.Icon, workspace => OmpPanel.Create(context, workspace))
        {
            Usable = () => status is not { Usable: false },
            Owns = tab => tab is TerminalTabRef terminal && context.Host().Terminals.GetValueOrDefault(terminal.WorkspaceId)?
                .FirstOrDefault(item => item.TabKey == terminal.TabKey)?.Agent?.Kind == OmpManifest.Id,
            Retarget = OmpPanel.Retarget,
            ShowTab = OmpPanel.ShowTab
        });
        context.Launcher(new AgentLauncher(OmpManifest.Id, "omp", OmpManifest.Icon, options => LaunchLine(context, options, status),
            () => status is null && context.Settings<OmpSettings>().Source != OmpSources.System
                ? new LauncherAvailability(false, "omp is still being checked.")
                : LauncherAvailability.Of(context.AgentLaunchContributions(OmpManifest.Id),
                    status is { Usable: false } ? status.Source == OmpSources.Managed ? status.Detail : "omp is not installed; see " + OmpLaunch.InstallUrl : null, status?.Usable))
        {
            // Core's Create-workspace and Project Home starts call this first: the download finishes before the terminal line is built.
            Open = async (workspace, options) =>
            {
                await Prepare();
                return false;
            },
            CreateIcon = (size, color) => new OmpGlyph(context, size, color as ISolidColorBrush),
            Models = () => [.. OmpLaunch.ModelSuggestions.Select(model => new LauncherModel(model, model))]
        });
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("omp-launch", (workspace, group) =>
        {
            var button = new Button
            {
                Name = "NewOmp",
                Content = new OmpGlyph(context, 16),
                Width = 32,
                Height = 32,
                Padding = new Thickness(8),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            AutomationProperties.SetName(button, "Start omp");
            ToolTip.SetTip(button, "Start omp (early support)");
            button.Click += async (_, _) =>
            {
                if (await Prepare() is not { } ready) return;
                await context.OpenTerminalAsync(workspace, new() { Command = LaunchLine(context, new(), ready), GroupId = group });
            };
            return button;
        }));
        context.WatchHost(host => string.Join("\n", host.Terminals.SelectMany(pair =>
            pair.Value.Where(tab => tab.Agent?.Kind == OmpManifest.Id).Select(tab => pair.Key + "\t" + tab.TabKey))), (_, _) => context.Invalidate());
        context.TabDecoration(tab => tab switch
        {
            TerminalTabRef terminal when context.Host().Terminals.GetValueOrDefault(terminal.WorkspaceId)?
                .FirstOrDefault(item => item.TabKey == terminal.TabKey)?.Agent?.Kind == OmpManifest.Id => new TabDecoration { Icon = OmpManifest.Icon },
            _ => null
        });
        _ = Probe();
        return () => settingsWatch.Dispose();
    }
}