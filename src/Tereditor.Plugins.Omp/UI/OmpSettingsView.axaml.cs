using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Omp.UI;

/// <summary>Settings › omp: where omp comes from (the pinned copy tereditor downloads, or the command), the documented launch flags with their suggested values, the MCP profile and code intelligence.</summary>
internal sealed partial class OmpSettingsView : AgentSettingsPage<OmpSettings, OmpNoParams, OmpStatus>
{
    private readonly AutoCompleteBox model, approval;
    private readonly CheckBox advisor, mcp, ownLsp;

    internal OmpSettingsView(IPluginUIContext context, Func<OmpStatus?> status, Action<OmpStatus> update) : base(context, "omp", OmpContract.Status, status, update)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<StackPanel>("ModelSourceHost")!.Children.Add(UiSlots.ModelSourceView(context, OmpManifest.Id));
        model = this.FindControl<AutoCompleteBox>("OmpModel")!;
        approval = this.FindControl<AutoCompleteBox>("OmpApprovalMode")!;
        advisor = this.FindControl<CheckBox>("OmpAdvisor")!;
        mcp = this.FindControl<CheckBox>("OmpMcp")!;
        ownLsp = this.FindControl<CheckBox>("OmpOwnLsp")!;
        model.ItemsSource = OmpLaunch.ModelSuggestions;
        approval.ItemsSource = OmpLaunch.ApprovalModes;
        BindText(model, () => model.Text, settings => settings.Model, (settings, value) => settings with { Model = value });
        BindText(approval, () => approval.Text, settings => settings.ApprovalMode, (settings, value) => settings with { ApprovalMode = value });
        advisor.Click += (_, _) => Save(settings => settings with { Advisor = advisor.IsChecked == true });
        mcp.Click += (_, _) => Save(settings => settings with { Mcp = mcp.IsChecked == true });
        ownLsp.Click += (_, _) => Save(settings => settings with { OwnCodeIntelligence = ownLsp.IsChecked == true });
        Start(
        [
            new(OmpSources.Managed, "Managed, pinned", "Run the omp that tereditor downloads, on Bun from Runtimes."),
            new(OmpSources.System, "System command", "Run the omp on your PATH.")
        ], "Launch command", "omp launch command", OmpContract.Install, OmpContract.CancelInstall, OmpContract.Remove);
    }

    protected override AgentDownload Download(OmpStatus state) => new(state.Installing, state.Progress, state.Error, state.PinnedVersion, state.ManagedVersion);

    protected override OmpStatus Starting(OmpStatus? state, string progress) =>
        (state ?? new OmpStatus(false, "omp") { Source = OmpSources.Managed }) with { Installing = true, Progress = progress, Error = "" };

    // What the page shows as chosen: the user's choice, else what the host settled on (managed while Bun is there), managed before it answers.
    protected override string ShownSource(OmpSettings settings)
    {
        var chosen = settings.Source.Length > 0 ? settings.Source : Status?.Source ?? OmpSources.Managed;
        return chosen == OmpSources.Managed ? OmpSources.Managed : OmpSources.System;
    }

    protected override (string Source, string Command) Saved(OmpSettings settings) => (settings.Source, settings.Command);

    protected override OmpSettings With(OmpSettings settings, string? source = null, string? command = null) =>
        settings with { Source = source ?? settings.Source, Command = command ?? settings.Command };

    protected override string SourceNote => Status?.SourceNote ?? "";

    protected override void Render(OmpSettings settings)
    {
        if (!model.IsKeyboardFocusWithin) model.Text = settings.Model;
        if (!approval.IsKeyboardFocusWithin) approval.Text = settings.ApprovalMode;
        advisor.IsChecked = settings.Advisor;
        mcp.IsChecked = settings.Mcp;
        ownLsp.IsChecked = settings.OwnCodeIntelligence;
    }
}