using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Omp.UI;

/// <summary>
/// The omp pane of a workspace: the installed version or the missing-binary state with the install link, the model provider, which
/// code intelligence sessions use, the profile note, and the context files omp reads for the workspace with token
/// estimates, imports nested under their importer, and offers to add missing files. All reads run on the host.
/// </summary>
internal sealed class OmpPanel : AgentConfigPane
{
    private readonly IPluginUIContext context;
    private readonly OmpPanelFrame frame = new();
    private readonly TextBlock version, missingText, missingNote, codeIntelligence, profileNote, error, signInResult;
    private readonly StackPanel missing, signIn, rows;
    private readonly ContentControl install, provider;
    private readonly Button importSignIn;
    private readonly WrapPanel offers;
    private bool installed, importing;
    // What the rows and offers last drawn were built from: an identical snapshot (every refresh, settings change and
    // re-attach asks again) leaves the controls, and the reader's place among them, alone.
    private string? rowsKey, offersKey;

    public static string CodeIntelligenceNote(OmpSettings settings) =>
        settings.OwnCodeIntelligence ? "Code intelligence: omp's own language servers" : "Code intelligence: tereditor's lsp_* tools";

    public static string? ProfileNote(OmpSettings settings) => settings.Mcp
        ? $"omp runs with its own \"{OmpLaunch.Profile}\" profile and sign-in. Copy your default sign-in below or run /login once in a tereditor omp tab."
        : null;

    public OmpPanel(IPluginUIContext context, string workspaceId)
        : base(workspaceId, "Omp", "omp", badgeTip: "Basic support: launch, MCP and context files work; deeper integration is still to come.")
    {
        this.context = context;
        Name = "OmpPanel";
        version = frame.FindControl<TextBlock>("OmpVersion")!;
        provider = frame.FindControl<ContentControl>("OmpProviderHost")!;
        missing = frame.FindControl<StackPanel>("OmpMissing")!;
        missingText = frame.FindControl<TextBlock>("OmpMissingText")!;
        install = frame.FindControl<ContentControl>("OmpInstallLink")!;
        missingNote = frame.FindControl<TextBlock>("OmpMissingNote")!;
        codeIntelligence = frame.FindControl<TextBlock>("OmpCodeIntelligence")!;
        profileNote = frame.FindControl<TextBlock>("OmpProfileNote")!;
        error = frame.FindControl<TextBlock>("OmpError")!;
        signIn = frame.FindControl<StackPanel>("OmpSignIn")!;
        importSignIn = frame.FindControl<Button>("OmpImportSignIn")!;
        signInResult = frame.FindControl<TextBlock>("OmpSignInResult")!;
        rows = frame.FindControl<StackPanel>("OmpContextRows")!;
        offers = frame.FindControl<WrapPanel>("OmpOffers")!;
        var link = Ui.Button("Install from omp.sh", () => TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(OmpLaunch.InstallUrl)));
        link.Name = "OmpInstall";
        install.Content = link;
        importSignIn.Click += (_, _) => _ = ImportSignInAsync();
        Children.Add(frame);
    }

    public override void ShowTab(AgentPaneTab tab)
    {
        foreach (var (page, name) in new[]
        {
            (AgentPaneTab.Context, "OmpContextTab"), (AgentPaneTab.Settings, "OmpSettingsTab"),
            (AgentPaneTab.Capabilities, "OmpCapabilitiesTab"), (AgentPaneTab.Account, "OmpAccountTab")
        })
            frame.FindControl<Control>(name)!.IsVisible = page == tab;
    }

    protected override IDisposable Watch() => context.OnSettings<OmpSettings>(_ => Reload());

    // The sign-in result and the install status are host-wide, not workspace data, so they stay.
    protected override void Forget()
    {
        rows.Children.Clear();
        offers.Children.Clear();
        rowsKey = offersKey = null;
        error.IsVisible = false;
    }

    protected override void Reload()
    {
        var current = NextRead();
        var owner = WorkspaceId;
        Notes(context.Settings<OmpSettings>());
        provider.Content = Account.Provider(AgentLaunchComposition.ProviderName(context.AgentLaunchContributions(OmpManifest.Id)) ?? "omp's own provider (built in)", "Omp");
        _ = Load();

        async Task Load()
        {
            try
            {
                var status = await context.RequestAsync(OmpContract.Status, new OmpNoParams());
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsCurrent(current)) return;
                    installed = status.Installed;
                    Notes(context.Settings<OmpSettings>());
                    var managed = status.Source == OmpSources.Managed;
                    version.Text = status.Installed ? "v" + status.Version + (managed ? " (pinned, managed by tereditor)" : "") : null;
                    missing.IsVisible = !status.Installed;
                    missingText.Text = status.Installed ? null
                        : status is { Installing: true } ? status.Progress
                        : status is { Error.Length: > 0 } ? status.Error
                        : managed && status.Usable ? "omp is not downloaded yet. Press Download in Settings, omp."
                        : managed ? status.Detail
                        : "omp is not installed. " + status.Detail;
                    missingNote.Text = managed ? "tereditor runs its own pinned copy of omp on Bun and never touches your global packages." : "tereditor uses the omp on your PATH and does not install it.";
                    install.IsVisible = !managed;
                });
            }
            catch (Exception) { }
            try
            {
                var snapshot = await context.RequestAsync(OmpContract.Context, new OmpWorkspaceParams(owner));
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsCurrent(current)) return;
                    error.IsVisible = false;
                    ShowContext(snapshot);
                });
            }
            catch (Exception failure)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsCurrent(current)) return;
                    error.Text = "Couldn't read omp's context files: " + failure.Message;
                    error.IsVisible = true;
                });
            }
        }
    }

    private void Notes(OmpSettings settings)
    {
        codeIntelligence.Text = CodeIntelligenceNote(settings);
        profileNote.Text = ProfileNote(settings);
        profileNote.IsVisible = profileNote.Text is not null;
        signIn.IsVisible = installed && settings.Mcp;
        frame.FindControl<TextBlock>("OmpAccountNothing")!.IsVisible = !signIn.IsVisible;
    }

    // Runs only on this click: the host pipes omp auth export into the tereditor profile's import.
    private async Task ImportSignInAsync()
    {
        if (importing) return;
        importing = true;
        importSignIn.IsEnabled = false;
        signInResult.Foreground = Ui.Muted;
        signInResult.Text = "Copying your omp sign-in…";
        signInResult.IsVisible = true;
        OmpSignInResult result;
        try { result = await context.RequestAsync(OmpContract.ImportSignIn, new OmpNoParams()); }
        catch (Exception) { result = new(false, "The host could not run the import."); }
        Dispatcher.UIThread.Post(() =>
        {
            importing = false;
            importSignIn.IsEnabled = true;
            signInResult.Foreground = result.Success ? Ui.Muted : Ui.Danger;
            signInResult.Text = result.Message;
        });
    }

    private void ShowContext(OmpContextSnapshot snapshot)
    {
        var nextRows = string.Join('\n', snapshot.Files.Select(file => $"{file.Path}|{file.Depth}|{file.Scope}|{file.Provider}|{file.Tokens}|{file.Shadowed}|{file.Cycle}"));
        var nextOffers = string.Join('\n', snapshot.Offers.Select(offer => $"{offer.Target}|{offer.Label}"));
        if (nextRows != rowsKey) ShowRows(snapshot, nextRows);
        if (nextOffers != offersKey) ShowOffers(snapshot, nextOffers);
    }

    private void ShowRows(OmpContextSnapshot snapshot, string key)
    {
        rowsKey = key;
        rows.Children.Clear();
        if (snapshot.Files.Count == 0) rows.Children.Add(Ui.Note("omp reads no context files here yet.", Ui.Hint, 12));
        foreach (var file in snapshot.Files)
        {
            var row = new DockPanel { Name = "OmpContextRow", Tag = file.Path, Margin = new Thickness(file.Depth * 16, 0, 0, 0) };
            var tokens = Ui.Text(file.Shadowed ? "shadowed" : file.Cycle ? "cycle" : "~" + file.Tokens.ToString("N0", CultureInfo.CurrentCulture) + " tok", Ui.Hint, 12);
            tokens.Name = "OmpContextTokens";
            SetDock(tokens, Dock.Right);
            row.Children.Add(tokens);
            var scope = ScopedSetting.ScopeChip(file.Depth > 0 ? "import" : file.Scope.ToString().ToLowerInvariant(), "OmpContextScope");
            scope.Margin = new Thickness(0, 0, 6, 0);
            SetDock(scope, Dock.Left);
            row.Children.Add(scope);
            var source = ScopedSetting.SourcePath(file.Path, path => OpenFile(context, path), "OmpContextSource");
            source.Opacity = file.Shadowed || file.Cycle ? 0.6 : 1;
            ToolTip.SetTip(source, file.Path + (file.Depth > 0 ? "" : $" ({file.Provider})"));
            row.Children.Add(source);
            rows.Children.Add(row);
        }
    }

    private void ShowOffers(OmpContextSnapshot snapshot, string key)
    {
        offersKey = key;
        offers.Children.Clear();
        foreach (var offer in snapshot.Offers)
        {
            var button = Ui.Button(offer.Label, () => _ = CreateAsync(offer.Target), "add");
            button.Name = "OmpOffer_" + offer.Target.ToString().ToLowerInvariant();
            button.Margin = new Thickness(0, 0, 8, 8);
            offers.Children.Add(button);
        }
    }

    private async Task CreateAsync(OmpContextTarget target)
    {
        var owner = WorkspaceId;
        try
        {
            var snapshot = await context.RequestAsync(OmpContract.CreateContextFile, new OmpCreateParams(owner, target));
            Dispatcher.UIThread.Post(() => { if (owner == WorkspaceId) ShowContext(snapshot); });
        }
        catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't create the omp context file"); }
    }
}