using Avalonia;
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
/// The OpenAI component: the shared key row (the key is stored through Model providers, never shown again) and the sign-in policy with a Sign in
/// action that runs Codex's own login in a terminal. tereditor starts no sign-in flow and reads no token.
/// </summary>
internal sealed class OpenAiView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly IPluginDependencyHandle providers;
    private readonly ApiKeyRow keyRow;
    private int generation;

    public OpenAiView(IPluginUIContext context)
    {
        this.context = context;
        providers = context.Dependency(ProvidersContract.Contract);
        Name = "ModelProvider-" + OpenAiProvider.ProviderId;
        Spacing = 4;
        keyRow = new ApiKeyRow("OpenAI",
            secret => Run(() => providers.RequestAsync(ProvidersContract.SaveModelKey, new SaveModelKeyParams(OpenAiProvider.ProviderId, secret)).AsTask()),
            () => Run(() => providers.RequestAsync(ProvidersContract.RemoveModelKey, new ModelProviderParams(OpenAiProvider.ProviderId)).AsTask()));
        Children.Add(keyRow);
        var signInLine = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var signIn = Ui.Button("Sign in", SignIn);
        signIn.Name = "OpenAiSignIn";
        ToolTip.SetTip(signIn, "Opens a terminal and runs: " + OpenAiProvider.SignInCommand);
        DockPanel.SetDock(signIn, Dock.Right);
        signInLine.Children.Add(signIn);
        signInLine.Children.Add(Ui.Text("Sign in with ChatGPT", Ui.TextBrush, 13));
        Children.Add(signInLine);
        var policy = Ui.Text(OpenAiProvider.SignInText, Ui.Muted, 12);
        policy.Name = "OpenAiSignInPolicy";
        policy.TextWrapping = TextWrapping.Wrap;
        Children.Add(policy);
        Show(null);
        AttachedToVisualTree += (_, _) => Run(() => providers.RequestAsync(ProvidersContract.Models, new ModelProvidersParams()).AsTask());
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
        keyRow.Hints.Clear();
        keyRow.Hints.Add(stored ? "API key stored. Codex can use OpenAI as its model source." : "No API key yet. Add one to let Codex use OpenAI as its model source.");
        keyRow.Show("OpenAI API" + (stored ? "  (ready)" : ""), stored);
    }
}