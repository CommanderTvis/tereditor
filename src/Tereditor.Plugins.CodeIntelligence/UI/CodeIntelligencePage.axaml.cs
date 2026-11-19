using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.CodeIntelligence.UI;

/// <summary>Shows a server's mark for its id.</summary>
internal sealed class ServerMarkConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is string server ? ServerNames.Mark(server) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Settings › Code Intelligence: the switch, the Java and Kotlin provider, each server's launcher, terms and tools, and the limits.
/// It reads and writes only through the plugin's own methods and channels; the host owns the state and the page follows its broadcast.
/// </summary>
internal sealed partial class CodeIntelligencePage : UserControl
{
    /// <summary>One server in the rail; <see cref="Missing"/> marks a tool its next start needs and cannot find.</summary>
    internal sealed record LspServerItem(string Id, bool Missing)
    {
        public override string ToString() => Id;
    }

    /// <summary>How long Install now waits before saying it is still running; the host keeps installing.</summary>
    internal static TimeSpan InstallTimeout { get; set; } = TimeSpan.FromSeconds(120);

    private readonly IPluginUIContext context;
    private readonly string workspace;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<IDisposable> subscriptions = [];
    private CodeIntelligenceSettingsState settings = new(true, "", "", "", "standard");
    private LanguageServerRow[] catalog = [];
    private Action? load;
    private Func<Task>? loadCatalog;
    private LanguageLimitsView? limits;

    /// <summary>The terms window Review and accept terms opened, while it is open.</summary>
    internal Window? TermsWindow { get; private set; }

    internal CodeIntelligencePage(IPluginUIContext context, string workspace)
    {
        this.context = context;
        this.workspace = workspace;
        AvaloniaXamlLoader.Load(this);
        Build();
        DetachedFromVisualTree += (_, _) =>
        {
            lifetime.Cancel();
            foreach (var subscription in subscriptions) subscription.Dispose();
            subscriptions.Clear();
        };
    }

    private T Part<T>(string name) where T : Control => this.FindControl<T>(name)!;

    // The host's own thread, never the UI thread: the in-process adapter runs the host half on its caller until its first real await.
    private Task<TResult> Call<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken token = default) =>
        Task.Run(async () => await context.RequestAsync(method, parameters, token), token);

    private static (bool AllowPath, string Command, string[]? Arguments) Options(string json, string id)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonObject all && all[id] is JsonObject entry)
                return (entry["allowPath"]?.GetValue<bool>() == true, entry["command"]?.GetValue<string>() ?? "",
                    entry["arguments"] is JsonArray arguments ? [.. arguments.Select(argument => argument?.GetValue<string>() ?? "")] : null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { }
        return (false, "", null);
    }

    private void Build()
    {
        var panel = (StackPanel)Content!;
        var provider = new LanguageProviderView(token => Call(CodeIntelligenceContract.Catalog, new LanguageServerParams(workspace), token), () => settings.JavaKotlinProvider,
            async chosen => (settings = await Call(CodeIntelligenceContract.SetProvider, new CodeIntelligenceValue(chosen))).JavaKotlinProvider);
        Part<ContentControl>("ProviderChoiceSettings").Content = provider;
        var selector = Part<ListBox>("LspServer");
        var allowPath = Part<CheckBox>("LspAllowPath");
        var resolved = Part<TextBlock>("LspResolved");
        var pathWarning = Part<TextBlock>("LspPathWarning");
        var command = Part<TextBox>("LspCommand");
        var defaultArguments = Part<CheckBox>("LspDefaultArguments");
        var arguments = Part<TextBox>("LspArguments");
        var message = Part<TextBlock>("LspMessage");
        Part<LinkedTextBlock>("LspImplementation").LinkClicked += ProviderLinks.OnLinkClicked;
        var save = Part<Button>("LspSave");
        var termsSection = Part<StackPanel>("LspTermsSection");
        var termsStatus = Part<TextBlock>("LspTermsStatus");
        var reviewTerms = Part<Button>("LspReviewTerms");
        var install = Part<Button>("LspInstall");
        var installRetry = Part<Button>("LspInstallRetry");
        var installStatus = Part<TextBlock>("LspInstallStatus");
        var installing = false;
        var termsAccepted = false;
        var termsConfirmed = false;
        var serverVersion = "";
        var installed = false;
        var dependencies = Part<LanguageDependencyList>("LspDependencies");
        var dependencySection = Part<StackPanel>("LspDependencySection");
        dependencies.SetTool = async (executable, path) => settings = await Call(CodeIntelligenceContract.SetTool, new LanguageToolParams(executable, path), lifetime.Token);
        dependencies.Status += (_, e) => { message.Foreground = Ui.Muted; message.Text = e.Text; };
        Part<TextBlock>("LspLifetimeNote").Text = Notes.Lifetime;
        var feature = Part<CheckBox>("LspFeatureEnabled");
        feature.IsChecked = settings.Enabled;
        feature.Click += async (_, _) =>
        {
            var value = feature.IsChecked == true;
            feature.IsEnabled = false;
            try { settings = await Call(CodeIntelligenceContract.SetEnabled, new CodeIntelligenceValue(value.ToString()), lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception failure) { message.Foreground = Ui.Danger; message.Text = failure.Message; }
            finally { feature.IsChecked = settings.Enabled; feature.IsEnabled = true; }
        };
        var website = Part<Button>("LspProviderWebsite");
        var repository = Part<Button>("LspProviderRepository");
        var links = Part<WrapPanel>("LspProviderLinks");
        website.Click += ProviderLinks.Open;
        repository.Click += ProviderLinks.Open;
        var schemas = new Dictionary<string, JsonNode>();
        // The settings the catalog's dependency probe depended on; a change (here or from another client) reprobes.
        var probed = (settings.LanguageTools, settings.LanguageServers);
        void Load()
        {
            provider.UpdateProvider(settings.JavaKotlinProvider);
            feature.IsChecked = settings.Enabled;
            if (probed != (settings.LanguageTools, settings.LanguageServers))
            {
                probed = (settings.LanguageTools, settings.LanguageServers);
                _ = LoadCatalog();
            }
            if (selector.SelectedItem is not LspServerItem { Id: var id }) return;
            Part<TextBlock>("LspServerHeading").Text = id;
            var options = Options(settings.LanguageServers, id);
            allowPath.IsChecked = options.AllowPath;
            defaultArguments.IsChecked = options.Arguments is null;
            arguments.IsEnabled = options.Arguments is not null;
            command.Text = options.Command; arguments.Text = string.Join('\n', options.Arguments ?? []);
            var server = catalog.First(server => server.Id == id);
            dependencies.Dependencies = server.Dependencies;
            dependencySection.IsVisible = server.Dependencies is { Count: > 0 };
            Part<LinkedTextBlock>("LspImplementation").Source = server.Detail.Length > 0 ? server.Detail : id;
            Part<TextBlock>("LspVersion").Text = server.Version == "toolchain"
                ? "Version: supplied by the host SDK" : "Pinned managed version: " + server.Version;
            message.Text = "";
            var hasTerms = server.HasTerms;
            termsSection.IsVisible = hasTerms;
            termsAccepted = installed = termsConfirmed = false;
            serverVersion = server.Version;
            installStatus.Text = "";
            installRetry.IsVisible = false;
            if (hasTerms)
            {
                // Until the host answers the acceptance is unknown: show the last known one, or nothing, never "not accepted".
                if (TermsAcceptanceCache.TryGet(id, serverVersion, out var cachedAt, out var cachedVersion)) ShowStatus(TermsKnowledge.Accepted, cachedAt, cachedVersion);
                else ShowStatus(TermsKnowledge.Checking);
            }
            links.IsVisible = false;
            _ = LoadLinks(id);
            _ = LoadResolution(id);
        }
        async Task LoadLinks(string id)
        {
            try
            {
                if (!schemas.TryGetValue(id, out var schema))
                {
                    var result = await Call(CodeIntelligenceContract.ConfigurationSchema, new LanguageServerParams(workspace, id), lifetime.Token);
                    if (result.State != "ready" || result.Schema.Length == 0) return;
                    schema = JsonNode.Parse(result.Schema)!;
                    schemas[id] = schema;
                }
                if (!lifetime.IsCancellationRequested && selector.SelectedItem is LspServerItem { Id: var selected } && selected == id)
                    ProviderLinks.Apply(schema, website, repository, links);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception failure) { if (!lifetime.IsCancellationRequested && selector.SelectedItem is LspServerItem { Id: var selected } && selected == id) message.Text = "Provider links unavailable: " + failure.Message; }
        }
        // justAccepted: the dialog recorded the acceptance a moment ago, so a server still missing is not "not accepted": Install now follows.
        async Task LoadResolution(string id, bool justAccepted = false)
        {
            resolved.Text = "Resolving the executable…"; resolved.Foreground = Ui.Muted; pathWarning.IsVisible = false;
            try
            {
                var result = await Call(CodeIntelligenceContract.Resolution, new LanguageServerParams(workspace, id), lifetime.Token);
                if (lifetime.IsCancellationRequested || selector.SelectedItem is not LspServerItem { Id: var selected } || selected != id) return;
                var source = result.Source switch { "managed" => "Managed", "path" => "PATH", "override" => "Executable override", _ => "None" };
                resolved.Foreground = result.State == "ready" ? Ui.Muted : Ui.Danger;
                resolved.Text = $"{source}: {(result.Path.Length > 0 ? result.Path : "no executable")}" + (result.Version.Length > 0 && result.Version != serverVersion ? $"\nVersion: {result.Version}" : "") + $"\n{result.Detail}\n{result.Pinning}";
                allowPath.IsEnabled = result.PathCapable;
                if (termsSection.IsVisible)
                {
                    installed = result.State == "ready" && result.Path.Length > 0;
                    if (!installed && justAccepted) UpdateTerms();
                    else if (!installed) { TermsAcceptanceCache.Forget(id); termsConfirmed = false; ShowStatus(TermsKnowledge.NotInstalled); }
                    else _ = LoadTerms(id);
                }
                pathWarning.Foreground = Ui.Danger; pathWarning.Text = result.Warning; pathWarning.IsVisible = result.Warning.Length > 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) when (!lifetime.IsCancellationRequested)
            {
                resolved.Foreground = Ui.Danger; resolved.Text = "Executable unavailable: " + failure.Message;
                if (termsSection.IsVisible) { termsConfirmed = true; ShowStatus(TermsKnowledge.Unavailable, detail: failure.Message); }
            }
        }
        void UpdateTerms()
        {
            reviewTerms.IsEnabled = !termsAccepted;
            install.IsVisible = termsAccepted && termsConfirmed && !installed;
            install.IsEnabled = !installing;
            installRetry.IsEnabled = !installing;
        }
        // Reading the terms of a server that is not installed downloads it, so only an installed one is read without the user asking.
        async Task LoadTerms(string id)
        {
            if (!installed) { UpdateTerms(); return; }
            try
            {
                var read = await Call(CodeIntelligenceContract.Terms, new LanguageTermsParams(workspace, id), lifetime.Token);
                if (lifetime.IsCancellationRequested || selector.SelectedItem is not LspServerItem { Id: var selected } || selected != id) return;
                ShowTerms(read);
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) when (!lifetime.IsCancellationRequested) { termsConfirmed = true; ShowStatus(TermsKnowledge.Unavailable, detail: failure.Message); }
        }
        void ShowStatus(TermsKnowledge knowledge, string acceptedAtUtc = "", string version = "", string detail = "")
        {
            var view = TermsStatusView.Of(knowledge, acceptedAtUtc, version, detail);
            termsAccepted = knowledge == TermsKnowledge.Accepted;
            termsStatus.Foreground = view.Failed ? Ui.Danger : Ui.Muted;
            termsStatus.Text = view.Text;
            reviewTerms.IsVisible = view.ShowReview;
            UpdateTerms();
            reviewTerms.IsEnabled = view.ReviewEnabled;
        }
        void ShowTerms(LanguageTermsState read)
        {
            termsConfirmed = true;
            if (read.State == "unavailable") { TermsAcceptanceCache.Forget(selector.SelectedItem?.ToString() ?? ""); ShowStatus(TermsKnowledge.Unavailable, detail: read.Detail); return; }
            var id = selector.SelectedItem?.ToString() ?? "";
            if (read.Accepted) TermsAcceptanceCache.Store(id, serverVersion, read.AcceptedAtUtc, read.Version); else TermsAcceptanceCache.Forget(id);
            ShowStatus(read.Accepted ? TermsKnowledge.Accepted : TermsKnowledge.NotAccepted, read.AcceptedAtUtc, read.Version);
        }
        reviewTerms.Click += (_, _) =>
        {
            if (selector.SelectedItem is not LspServerItem { Id: var id } || TopLevel.GetTopLevel(reviewTerms) is not Window owner) return;
            var dialog = new LanguageTermsWindow((request, token) => Call(CodeIntelligenceContract.Terms, request, token), workspace, id);
            TermsWindow = dialog;
            dialog.Closed += (_, _) =>
            {
                TermsWindow = null;
                if (dialog.Result is { } outcome && selector.SelectedItem is LspServerItem { Id: var current } && current == id) { installed = true; ShowTerms(outcome); _ = LoadResolution(id, outcome.Accepted); }
            };
            dialog.Show(owner);
        };
        async Task Install()
        {
            if (installing || selector.SelectedItem is not LspServerItem { Id: var id }) return;
            installing = true; installRetry.IsVisible = false; UpdateTerms();
            installStatus.Foreground = Ui.Muted; installStatus.Text = "Installing. A first install can take several minutes.";
            var call = Call(CodeIntelligenceContract.Provision, new LanguageServerParams(workspace, id), lifetime.Token);
            try
            {
                var done = await Task.WhenAny(call, Task.Delay(InstallTimeout, lifetime.Token));
                if (lifetime.IsCancellationRequested) return;
                if (done != call)
                {
                    // The install keeps running in the host; looking again shows whether it finished.
                    installStatus.Text = "Still installing. Choose Retry later to check again.";
                    installRetry.IsVisible = true;
                    return;
                }
                var result = await call;
                if (selector.SelectedItem is not LspServerItem { Id: var current } || current != id) return;
                installStatus.Foreground = result.State == "ready" ? Ui.Muted : Ui.Danger;
                installStatus.Text = result.State == "ready" ? "Installed." : "Install failed: " + result.Detail;
                installRetry.IsVisible = result.State != "ready";
                if (result.State == "ready") { installed = true; _ = LoadResolution(id); }
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) when (!lifetime.IsCancellationRequested) { installStatus.Foreground = Ui.Danger; installStatus.Text = "Install failed: " + failure.Message; installRetry.IsVisible = true; }
            finally { installing = false; UpdateTerms(); }
        }
        install.Click += async (_, _) => await Install();
        installRetry.Click += async (_, _) => await Install();
        void ArgumentControls()
        {
            defaultArguments.IsEnabled = !string.IsNullOrWhiteSpace(command.Text);
            arguments.IsEnabled = defaultArguments.IsEnabled && defaultArguments.IsChecked != true;
        }
        defaultArguments.IsCheckedChanged += (_, _) => ArgumentControls();
        command.TextChanged += (_, _) => ArgumentControls();
        selector.SelectionChanged += (_, _) => Load();
        save.IsEnabled = false;
        save.Click += async (_, _) =>
        {
            if (selector.SelectedItem is not LspServerItem { Id: var id }) return;
            var args = (arguments.Text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var options = new LanguageServerOptionsParams(id, allowPath.IsChecked == true, (command.Text ?? "").Trim(), string.IsNullOrWhiteSpace(command.Text) || defaultArguments.IsChecked == true ? null : args);
            save.IsEnabled = false;
            try
            {
                settings = await Call(CodeIntelligenceContract.SetServerOptions, options, lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                message.Foreground = Ui.Muted; message.Text = "Saved. Applies on the next Start / Retry.";
                _ = LoadResolution(id);
            }
            catch (Exception failure) when (!lifetime.IsCancellationRequested) { message.Foreground = Ui.Danger; message.Text = failure.Message; }
            catch (OperationCanceledException) { }
            finally { save.IsEnabled = true; }
        };
        limits = new LanguageLimitsView(token => Call(CodeIntelligenceContract.Running, new LanguageServerParams(workspace), token), () => settings.Limits,
            async json => settings = await Call(CodeIntelligenceContract.SetLimits, new CodeIntelligenceValue(json)));
        panel.Children.Add(limits);
        load = Load;
        async Task LoadCatalogAsync()
        {
            try
            {
                var result = await Call(CodeIntelligenceContract.Catalog, new LanguageServerParams(workspace), lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                ServerNames.Remember(result.Servers);
                catalog = [.. result.Servers.OrderBy(server => server.Id, StringComparer.Ordinal)];
                var previous = (selector.SelectedItem as LspServerItem)?.Id;
                var items = catalog.Select(server => new LspServerItem(server.Id, LanguageDependencyList.HasMissing(server.Dependencies))).ToArray();
                selector.ItemsSource = items;
                selector.SelectedItem = items.FirstOrDefault(item => item.Id == previous) ?? items.FirstOrDefault();
                Part<TextBlock>("LspStorage").Text = result.Detail;
                save.IsEnabled = catalog.Length > 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) { message.Foreground = Ui.Danger; message.Text = failure.Message; }
        }
        loadCatalog = LoadCatalogAsync;
        // The host's state, then every change; the catalog follows the plugins that activate and deactivate.
        subscriptions.Add(context.Subscribe(CodeIntelligenceContract.Settings, state => { settings = state; limits.Refresh(); load?.Invoke(); }, new CodeIntelligenceSettingsParams()));
        subscriptions.Add(context.Subscribe(CodeIntelligenceContract.CatalogChanged, change => _ = LoadCatalog()));
        _ = LoadCatalog();
    }

    private Task LoadCatalog() => loadCatalog?.Invoke() ?? Task.CompletedTask;
}