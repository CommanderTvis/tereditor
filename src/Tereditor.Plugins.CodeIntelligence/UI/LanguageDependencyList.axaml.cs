using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.CodeIntelligence.UI;

/// <summary>
/// The host tools one language server declares, each with the user's ways to provide it: a host-wide path of their own, or the install hint.
/// Paths change through the host, and the page refreshes on its broadcast.
/// </summary>
internal sealed partial class LanguageDependencyList : UserControl
{
    /// <summary>One host tool a server needs, as a row.</summary>
    internal sealed record DependencyRow(LanguageDependencyRow Dependency, bool CanChangePaths)
    {
        public bool Missing => Dependency.State is "missing" or "rejected";
        public string Text => Dependency.State switch
        {
            "found" => $"{Dependency.Executable}: {Dependency.Path} ({Dependency.Source switch { "override" => "your path", "home" => "from its home variable", _ => "PATH" }})",
            "download" => Dependency.Problem,
            "rejected" => $"{Dependency.Executable}: your path cannot run. {Dependency.Problem}",
            _ => NeedsText(Dependency) + (Dependency.Problem.Length > 0 ? " " + Dependency.Problem : "")
        };
        public bool ShowText => !(OmitMissing && Missing && Dependency.Required);
        public bool OmitMissing { get; init; }
        public string Hint => Dependency.InstallHint;
        public Avalonia.Media.IBrush Brush => Missing && Dependency.Required ? Ui.Danger : Ui.Muted;
        public bool CanChange => CanChangePaths;
        public bool CanClear => CanChangePaths && Dependency.Override;
    }

    /// <summary>The one wording of a missing tool, by its name: <c>Needs PowerShell 7</c>.</summary>
    internal static string NeedsText(LanguageDependencyRow dependency) => $"Needs {dependency.Name}";

    /// <summary>Whether any tool the next managed start needs cannot be found.</summary>
    internal static bool HasMissing(IReadOnlyList<LanguageDependencyRow>? dependencies) => (dependencies ?? []).Any(item => item.Required && item.State is not ("found" or "download"));

    public static readonly StyledProperty<IReadOnlyList<LanguageDependencyRow>?> DependenciesProperty =
        AvaloniaProperty.Register<LanguageDependencyList, IReadOnlyList<LanguageDependencyRow>?>(nameof(Dependencies));

    /// <summary>Raised with a message for the page's status line.</summary>
    public static readonly RoutedEvent<StatusEventArgs> StatusEvent =
        RoutedEvent.Register<LanguageDependencyList, StatusEventArgs>(nameof(Status), RoutingStrategies.Bubble);

    internal sealed class StatusEventArgs(string text) : RoutedEventArgs(StatusEvent) { public string Text { get; } = text; }

    public IReadOnlyList<LanguageDependencyRow>? Dependencies { get => GetValue(DependenciesProperty); set => SetValue(DependenciesProperty, value); }
    public event EventHandler<StatusEventArgs>? Status { add => AddHandler(StatusEvent, value); remove => RemoveHandler(StatusEvent, value); }

    private Func<string, string, Task>? setTool;

    /// <summary>Saves a tool's host-wide path; null hides Use path and Clear path.</summary>
    internal Func<string, string, Task>? SetTool { get => setTool; set { setTool = value; Rebuild(); } }

    public LanguageDependencyList()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(LinkedTextBlock.LinkClickedEvent, ProviderLinks.OnLinkClicked);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DependenciesProperty) Rebuild();
    }

    private void Rebuild()
    {
        var rows = this.FindControl<ItemsControl>("DependencyRows");
        if (rows is null) return;
        var next = (Dependencies ?? []).Where(item => item.Required).Select(item => new DependencyRow(item, SetTool is not null)).ToArray();
        if (!(rows.ItemsSource?.Cast<DependencyRow>() ?? []).SequenceEqual(next)) rows.ItemsSource = next;
    }

    private void Report(string text) => RaiseEvent(new StatusEventArgs(text));

    private async void UseDependencyPathClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: DependencyRow row } || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new() { Title = $"Choose {row.Dependency.Executable} ({row.Dependency.Name})", AllowMultiple = false });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Choose a local file.");
            await SetToolAsync(row.Dependency.Executable, path);
        }
        catch (Exception error) { Report("Couldn't use that path: " + error.Message); }
    }

    private async void ClearDependencyPathClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: DependencyRow row }) return;
        try { await SetToolAsync(row.Dependency.Executable, ""); }
        catch (Exception error) { Report("Couldn't clear the path: " + error.Message); }
    }

    /// <summary>Changes the host-wide tool path through the host; the page's rows update when its broadcast arrives.</summary>
    internal async Task SetToolAsync(string executable, string path)
    {
        if (SetTool is not { } set) return;
        await Task.Run(async () => await set(executable, path));
        Report(path.Length > 0 ? $"Using {path} for {executable}. Start / Retry to use it." : $"{executable} is found on PATH again.");
    }

    private async void CopyDependencyHintClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: DependencyRow row }) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("No clipboard.");
            await clipboard.SetTextAsync(row.Hint);
            Report("Install hint copied.");
        }
        catch (Exception error) { Report("Couldn't copy the hint: " + error.Message); }
    }
}