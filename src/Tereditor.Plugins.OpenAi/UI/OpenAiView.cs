using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.OpenAi.UI;

/// <summary>
/// The OpenAI component: the key path (stored through Model providers, never shown again) and the sign-in policy with a Sign in
/// action that runs Codex's own login in a terminal. tereditor starts no sign-in flow and reads no token.
/// </summary>
internal sealed class OpenAiView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly IPluginDependencyHandle providers;
    private readonly TextBlock state = Ui.Text("", Ui.Muted, 12);
    private readonly TextBox key = new() { PasswordChar = '•', PlaceholderText = "OpenAI API key", Name = "OpenAiKey", MinWidth = 280 };
    private readonly Button remove;
    private int generation;

    public OpenAiView(IPluginUIContext context)
    {
        this.context = context;
        providers = context.Dependency(ProvidersContract.Contract);
        Name = "OpenAiComponent";
        Spacing = 6;
        state.Name = "OpenAiKeyState";
        state.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetName(key, "API key for OpenAI");
        Children.Add(Ui.Text("OpenAI", Ui.TextBrush, 13));
        Children.Add(state);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        var save = Ui.Button("Save key", Save);
        save.Name = "OpenAiSave";
        remove = Ui.Button("Remove key", () => Run(() => providers.RequestAsync(ProvidersContract.RemoveModelKey, new ModelProviderParams(OpenAiProvider.ProviderId)).AsTask()));
        remove.Name = "OpenAiRemove";
        Grid.SetColumn(save, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(key);
        row.Children.Add(save);
        row.Children.Add(remove);
        key.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Save(); e.Handled = true; } };
        Children.Add(row);
        var policy = Ui.Text(OpenAiProvider.SignInText, Ui.Muted, 12);
        policy.Name = "OpenAiSignInPolicy";
        policy.TextWrapping = TextWrapping.Wrap;
        Children.Add(policy);
        var signIn = Ui.Button("Sign in", SignIn);
        signIn.Name = "OpenAiSignIn";
        signIn.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(signIn, "Opens a terminal and runs: " + OpenAiProvider.SignInCommand);
        Children.Add(signIn);
        Show(null);
        AttachedToVisualTree += (_, _) => Run(() => providers.RequestAsync(ProvidersContract.Models, new ModelProvidersParams()).AsTask());
    }

    private void Save()
    {
        var secret = key.Text ?? "";
        key.Text = "";
        if (secret.Trim().Length == 0) return;
        Run(() => providers.RequestAsync(ProvidersContract.SaveModelKey, new SaveModelKeyParams(OpenAiProvider.ProviderId, secret)).AsTask());
    }

    private void SignIn()
    {
        if (context.Host().ActiveWorkspaceId is not { } workspace)
        {
            context.Notify(PluginNotificationKind.Info, "Open a workspace first", "The sign-in runs in a terminal tab of a workspace.");
            return;
        }
        _ = Open(workspace);
        async Task Open(string id)
        {
            try { await context.OpenTerminalAsync(id, new TerminalOpenOptions { Command = OpenAiProvider.SignInCommand }); }
            catch (Exception error) when (error is not OperationCanceledException) { context.Notify(PluginNotificationKind.Error, "Couldn't open the terminal", error.Message); }
        }
    }

    private void Run(Func<Task<ModelProvidersReport>> request)
    {
        var mine = ++generation;
        _ = Go();
        async Task Go()
        {
            try
            {
                var report = await request();
                if (mine == generation) Show(report);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (mine == generation) context.Notify(PluginNotificationKind.Error, "Couldn't change the OpenAI key", error.Message);
            }
        }
    }

    private void Show(ModelProvidersReport? report)
    {
        var stored = report is not null && OpenAiProvider.KeyStored(report);
        state.Text = stored ? "API key stored. Codex can use OpenAI as its model source." : "No API key yet. Add one to let Codex use OpenAI as its model source.";
        remove.IsVisible = stored;
    }
}
