using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Tereditor.Plugins.CodeIntelligence.UI;

internal sealed partial class LanguageProviderView : UserControl
{
    private readonly Func<CancellationToken, Task<LanguageCatalogState>> readCatalog;
    private readonly Func<string, Task<string>> save;
    private readonly Func<string> current;
    private CancellationTokenSource? lifetime;
    private string savedProvider = "standard";
    /// <summary>The choices: <c>standard</c>, then each server that replaces others (H21).</summary>
    private string[] choices = ["standard"];
    private HashSet<string> withTerms = [];

    internal LanguageProviderView(Func<CancellationToken, Task<LanguageCatalogState>> readCatalog, Func<string> current, Func<string, Task<string>> save)
    {
        this.readCatalog = readCatalog; this.current = current; this.save = save;
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += async (_, _) =>
        {
            lifetime = new();
            var scope = lifetime;
            try
            {
                var catalog = await Task.Run(async () => await readCatalog(scope.Token), scope.Token);
                if (lifetime != scope) return;
                ServerNames.Remember(catalog.Servers);
                var replacing = catalog.Servers.Where(server => server.Replaces is { Count: > 0 }).ToArray();
                choices = ProviderOptions.Available(catalog.Servers.Select(server => server.Id), replacing.Select(server => (server.Id, server.Replaces!)));
                withTerms = replacing.Where(server => server.HasTerms).Select(server => server.Id).ToHashSet();
                var replaced = replacing.SelectMany(server => server.Replaces!).Distinct().ToArray();
                var label = replacing.Select(server => server.ReplacesLabel).FirstOrDefault(text => text.Length > 0) ?? string.Join(" / ", replaced.Select(ServerNames.Display));
                this.FindControl<TextBlock>("ProviderChoiceHeading")!.Text = label + " provider · all projects on this host";
                providerChoiceLoading = true;
                var standardLabel = string.Join(" + ", replaced.Select(ServerNames.Display));
                this.FindControl<ComboBox>("ProviderChoice")!.ItemsSource = choices.Select(id => id == ProviderOptions.Standard ? standardLabel : ServerNames.Display(id)).ToArray();
                providerChoiceLoading = false;
                savedProvider = current();
                RefreshProviderChoice();
            }
            catch (OperationCanceledException) when (scope.IsCancellationRequested) { }
            catch (Exception error) { if (lifetime == scope) this.FindControl<TextBlock>("ProviderChoiceMessage")!.Text = error.Message; }
        };
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    internal void UpdateProvider(string provider)
    {
        savedProvider = provider;
        RefreshProviderChoice();
    }

    private bool providerChoiceLoading;
    private bool providerChoiceDirty;
    private bool providerChoiceSaving;
    private int savedProviderChoice;

    /// <summary>Offered only while its plugins are enabled: one option is a plain disabled choice, none hides the section, and a saved choice that is not offered shows the one in effect.</summary>
    private void RefreshProviderChoice()
    {
        this.FindControl<StackPanel>("ProviderChoiceSection")!.IsVisible = choices.Length > 0;
        var effective = ProviderOptions.Effective(savedProvider, choices);
        savedProviderChoice = Math.Max(0, Array.IndexOf(choices, effective));
        var single = choices.Length == 1;
        this.FindControl<ComboBox>("ProviderChoice")!.IsEnabled = !single && !providerChoiceSaving;
        this.FindControl<Button>("ApplyProviderChoice")!.IsVisible = !single;
        this.FindControl<TextBlock>("ProviderChoiceEffective")!.Text = effective != savedProvider && choices.Length > 0
            ? $"The saved choice's plugins are off, so {(effective == ProviderOptions.Standard ? "the standard providers are" : ServerNames.Display(effective) + " is")} in effect." : "";
        if (!providerChoiceDirty && !providerChoiceSaving)
        {
            providerChoiceLoading = true;
            this.FindControl<ComboBox>("ProviderChoice")!.SelectedIndex = savedProviderChoice;
            providerChoiceLoading = false;
        }
        this.FindControl<Button>("ApplyProviderChoice")!.IsEnabled = !providerChoiceSaving && this.FindControl<ComboBox>("ProviderChoice")!.SelectedIndex != savedProviderChoice;
    }

    private void ProviderChoiceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (providerChoiceLoading) return;
        providerChoiceDirty = this.FindControl<ComboBox>("ProviderChoice")!.SelectedIndex != savedProviderChoice;
        this.FindControl<Button>("ApplyProviderChoice")!.IsEnabled = providerChoiceDirty && !providerChoiceSaving;
    }

    private async void ApplyProviderChoiceClicked(object? sender, RoutedEventArgs e)
    {
        if (providerChoiceSaving || lifetime is not { } scope) return;
        var choice = this.FindControl<ComboBox>("ProviderChoice")!.SelectedIndex;
        if (choice < 0 || choice >= choices.Length) return;
        var chosen = choices[choice];
        providerChoiceSaving = true;
        this.FindControl<ComboBox>("ProviderChoice")!.IsEnabled = false;
        this.FindControl<Button>("ApplyProviderChoice")!.IsEnabled = false;
        var message = this.FindControl<TextBlock>("ProviderChoiceMessage")!;
        message.Text = "Saving provider choice…";
        try
        {
            var saved = await Task.Run(async () => await save(chosen), scope.Token);
            if (lifetime != scope) return;
            providerChoiceDirty = false;
            savedProvider = saved;
            RefreshProviderChoice();
            message.Text = withTerms.Contains(chosen)
                ? $"Provider choice saved. {ServerNames.Display(chosen)} needs its license terms accepted before it can start: select it in the server list below and choose Review and accept terms."
                : "Provider choice saved for every project on this host. Start a server when ready.";
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == scope) message.Text = error.Message; }
        finally
        {
            providerChoiceSaving = false;
            if (lifetime == scope)
            {
                this.FindControl<ComboBox>("ProviderChoice")!.IsEnabled = true;
                RefreshProviderChoice();
            }
        }
    }
}