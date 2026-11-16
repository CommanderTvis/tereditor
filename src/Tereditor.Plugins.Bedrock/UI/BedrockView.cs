using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Bedrock.UI;

/// <summary>
/// The Amazon Bedrock component: where the platform's models are (non-secret settings, saved through the host) and whether Claude Code is offered them.
/// It asks for no key and shows none: the credentials are the platform's own.
/// </summary>
internal sealed class BedrockView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly TextBox region = new() { PlaceholderText = "AWS Region, such as us-east-1", MinWidth = 220 };
    private readonly TextBox profile = new() { PlaceholderText = "AWS profile (optional)", MinWidth = 220 };
    private readonly TextBlock state = Ui.Text("", Ui.Muted, 12);

    public BedrockView(IPluginUIContext context)
    {
        this.context = context;
        Name = "BedrockComponent";
        Spacing = 6;
        state.Name = "BedrockState";
        state.TextWrapping = TextWrapping.Wrap;
        region.Name = "BedrockRegion";
        AutomationProperties.SetName(region, "AWS Region, such as us-east-1");
        profile.Name = "BedrockProfile";
        AutomationProperties.SetName(profile, "AWS profile (optional)");
        Children.Add(Ui.Text(BedrockPlatform.Name, Ui.TextBrush, 13));
        var note = Ui.Text(BedrockPlatform.Note, Ui.Muted, 12);
        note.TextWrapping = TextWrapping.Wrap;
        Children.Add(note);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(region);
        row.Children.Add(profile);
        var save = Ui.Button("Save", Save);
        save.Name = "BedrockSave";
        row.Children.Add(save);
        Children.Add(row);
        Children.Add(state);
        Load(context.Settings<BedrockSettings>());
        context.OnSettings<BedrockSettings>(Load);
    }

    private void Load(BedrockSettings settings)
    {
        region.Text = settings.Region;
        profile.Text = settings.Profile;
        state.Text = BedrockPlatform.Problem(settings) ?? "Ready. Claude Code can use Amazon Bedrock as its model source.";
    }

    private void Save()
    {
        var next = new BedrockSettings { Region = region.Text?.Trim() ?? "", Profile = profile.Text?.Trim() ?? "" };
        _ = Go();
        async Task Go()
        {
            try { await context.UpdateSettingsAsync(next); }
            catch (Exception error) when (error is not OperationCanceledException) { context.Notify(PluginNotificationKind.Error, "Couldn't save the Amazon Bedrock settings", error.Message); }
        }
    }
}