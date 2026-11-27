using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Pi.UI;

/// <summary>Settings › Pi: where Pi comes from (the command, or the pinned copy tereditor downloads) and the model and thinking level a new chat starts with.</summary>
internal sealed partial class PiSettingsView : AgentSettingsPage<PiSettings, PiNoParams, PiStatus>
{
    private readonly IPluginUIContext context;
    private readonly TextBlock version;
    private readonly AutoCompleteBox model;
    private readonly OptionCards thinking;
    private readonly CheckBox codemode;

    internal PiSettingsView(IPluginUIContext context, Func<PiStatus?> status, Action<PiStatus?> update) : base(context, "Pi", PiContract.Status, status, update)
    {
        this.context = context;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<StackPanel>("ModelSourceHost")!.Children.Add(UiSlots.ModelSourceView(context, PiManifest.Id));
        version = this.FindControl<TextBlock>("PiVersion")!;
        model = this.FindControl<AutoCompleteBox>("PiModel")!;
        thinking = this.FindControl<OptionCards>("PiThinking")!;
        codemode = this.FindControl<CheckBox>("PiCodemode")!;
        model.ItemsSource = ModelChoices();
        model.GotFocus += (_, _) => model.IsDropDownOpen = true;
        model.AddHandler(InputElement.PointerPressedEvent, (_, _) => model.IsDropDownOpen = true, handledEventsToo: true);
        model.ItemTemplate = new FuncDataTemplate<string>((id, _) => id is null ? null : new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = id, FontFamily = Ui.CodeFont },
                new TextBlock { Text = PiSources.ModelSuggestions.FirstOrDefault(suggestion => suggestion.Id == id).Description ?? "Listed by an open chat", Foreground = Ui.Muted }
            }
        });
        thinking.SetOptions(ThinkingOptions(""), null);
        BindText(model, () => model.Text, settings => settings.Model, (settings, value) => settings with { Model = value });
        thinking.Chosen += level =>
        {
            if (level != context.Settings<PiSettings>().Thinking) Save(settings => settings with { Thinking = level });
        };
        codemode.Click += (_, _) => Save(settings => settings with { Codemode = codemode.IsChecked == true });
        Start(
        [
            new(PiSources.System, "System command", "Run the Pi on your PATH."),
            new(PiSources.Managed, "Managed, pinned", "Run the pinned Pi that tereditor downloads.")
        ], "Command", "pi command", PiContract.Install, PiContract.CancelInstall, PiContract.Remove);
    }

    protected override AgentDownload Download(PiStatus state) => new(state.Installing, state.Progress, state.Error, state.PinnedVersion, state.ManagedVersion);

    protected override PiStatus Starting(PiStatus? state, string progress) =>
        (state ?? new PiStatus(false, PiSources.Managed)) with { Installing = true, Progress = progress, Error = "" };

    protected override string ShownSource(PiSettings settings) => settings.Source == PiSources.Managed ? PiSources.Managed : PiSources.System;

    protected override (string Source, string Command) Saved(PiSettings settings) => (settings.Source, settings.Command);

    protected override PiSettings With(PiSettings settings, string? source = null, string? command = null) =>
        settings with { Source = source ?? settings.Source, Command = command ?? settings.Command };

    protected override void Render(PiSettings settings)
    {
        model.ItemsSource = ModelChoices();
        if (!model.IsKeyboardFocusWithin) model.Text = settings.Model;
        if (thinking.Options.Count != ThinkingOptions(settings.Thinking).Count) thinking.SetOptions(ThinkingOptions(settings.Thinking), null);
        thinking.Show(settings.Thinking);
        codemode.IsChecked = settings.Codemode;
        version.Text = Status switch
        {
            { Source: PiSources.Managed, Installed: true, Version: { } pinned } => "Pi v" + pinned + " (pinned)",
            { Source: PiSources.Managed } => "",
            { Installed: true, Version: { } found } => "Pi v" + found,
            { Installed: true } => "Pi is installed.",
            { Detail: { } detail } => "Pi is not installed: " + detail,
            _ => ""
        };
    }

    private string[] ModelChoices() => [.. PiSources.ModelSuggestions.Select(suggestion => suggestion.Id).Concat(Status?.Models ?? []).Distinct()];

    /// <summary>The thinking cards: Pi's own default first, then each level; a saved value Pi's docs do not list gets a card of its own.</summary>
    private static List<OptionCard> ThinkingOptions(string saved)
    {
        var options = new List<OptionCard> { new("", "Default", "Use Pi's own setting.") };
        options.AddRange(PiSources.ThinkingLevels.Select(level => new OptionCard(level.Id, level.Id, level.Description)));
        if (saved.Length > 0 && options.All(option => option.Id != saved)) options.Add(new(saved, saved, "Set earlier."));
        return options;
    }
}