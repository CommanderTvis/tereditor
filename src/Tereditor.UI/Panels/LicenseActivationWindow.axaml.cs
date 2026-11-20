using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using Tereditor.Host.Abstractions;

namespace Tereditor.UI.Panels;

/// <summary>
/// The one window that activates a server's license with what the user holds: an activation code, the offline activation zip or file
/// (a path on the workspace host), or a license server. Every place that shows the license opens it. It sends the value through the
/// host's license action and never keeps, logs or stores it: the field is cleared when the window closes.
/// </summary>
internal sealed partial class LicenseActivationWindow : Window
{
    /// <summary>Runs a license action and returns the failure text, or <see langword="null"/> when it worked.</summary>
    internal delegate Task<string?> Submit(string action, JsonObject arguments, CancellationToken token);

    private static readonly (string Action, string Argument, string Help, string Prompt)[] Kinds =
    [
        ("code", "activationCode", "Paste the activation code from your JetBrains Account.", "Activation code"),
        ("codeFile", "licenseFile", "Give the path of the offline activation zip from account.jetbrains.com. The host reads it, so the path is on the machine that runs the workspace.", "Path of the zip or file"),
        ("serverAdd", "serverAddress", "Give the address of your license server. It starts with https:// or http://.", "License server, for example https://licenses.example.com")
    ];

    private readonly Submit submit;
    private readonly Action? activated;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;

    /// <summary>The window while it is open, so a check can reach it.</summary>
    internal static LicenseActivationWindow? Current { get; private set; }

    internal LicenseActivationWindow(Submit submit, bool canBrowse, Action? activated = null)
    {
        this.submit = submit; this.activated = activated;
        AvaloniaXamlLoader.Load(this);
        Find<TextBlock>("ActivationHeading").PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        Find<ComboBox>("ActivationKind").SelectionChanged += (_, _) => ShowKind(canBrowse);
        Find<Button>("ActivationSubmit").Click += async (_, _) => await ActivateAsync();
        Find<Button>("ActivationCancel").Click += (_, _) => Close();
        Find<Button>("ActivationBrowse").Click += async (_, _) => await BrowseAsync();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Opened += (_, _) => Find<TextBox>("ActivationInput").Focus();
        Closed += (_, _) =>
        {
            Find<TextBox>("ActivationInput").Text = "";
            lifetime.Cancel(); lifetime.Dispose();
            if (Current == this) Current = null;
        };
        Current = this;
        ShowKind(canBrowse);
    }

    private T Find<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void ShowKind(bool canBrowse)
    {
        var kind = Kinds[Math.Max(0, Find<ComboBox>("ActivationKind").SelectedIndex)];
        Find<TextBlock>("ActivationHelp").Text = kind.Help;
        Find<TextBox>("ActivationInput").PlaceholderText = kind.Prompt;
        Find<Button>("ActivationBrowse").IsVisible = canBrowse && kind.Action == "codeFile";
        Find<TextBlock>("ActivationError").IsVisible = false;
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "Choose the license zip or file", AllowMultiple = false });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) Find<TextBox>("ActivationInput").Text = path;
        }
        catch (Exception error) when (error is not OperationCanceledException) { Fail("Cannot choose a file: " + error.Message); }
    }

    private async Task ActivateAsync()
    {
        if (busy) return;
        var kind = Kinds[Math.Max(0, Find<ComboBox>("ActivationKind").SelectedIndex)];
        var value = Find<TextBox>("ActivationInput").Text ?? "";
        if (value.Trim().Length == 0) { Fail("Enter a value first."); return; }
        Busy(true);
        string? failure;
        try { failure = await submit(kind.Action, new JsonObject { [kind.Argument] = value }, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { failure = error.Message; }
        if (lifetime.IsCancellationRequested) return;
        Busy(false);
        if (!string.IsNullOrEmpty(failure)) { Fail(failure); return; }
        Close();
        activated?.Invoke();
    }

    private void Busy(bool on)
    {
        busy = on;
        Find<ProgressBar>("ActivationProgress").IsVisible = on;
        foreach (var name in new[] { "ActivationSubmit", "ActivationBrowse", "ActivationKind", "ActivationInput" }) Find<Control>(name).IsEnabled = !on;
        if (on) Find<TextBlock>("ActivationError").IsVisible = false;
    }

    private void Fail(string text)
    {
        var error = Find<TextBlock>("ActivationError");
        error.Text = text; error.IsVisible = true;
    }

    /// <summary>
    /// Runs a license action of <paramref name="server"/> on the host, off the UI thread, and returns the failure text or <see langword="null"/>.
    /// The caller refreshes the inventory after it worked.
    /// </summary>
    internal static async Task<string?> HostSubmitAsync(IProjectServices host, string workspace, string server, string action, JsonObject arguments, CancellationToken token)
    {
        var result = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "action", Server: server, Query: action, Configuration: arguments.ToJsonString()), token), token);
        var outcome = result.State == "ready" ? JsonNode.Parse(result.Detail) as JsonObject : null;
        if (outcome is null) return result.Detail;
        return outcome["ok"]?.GetValue<bool>() == true ? null : outcome["message"]?.ToString() is { Length: > 0 } message ? message : "That did not work. Try again.";
    }
}