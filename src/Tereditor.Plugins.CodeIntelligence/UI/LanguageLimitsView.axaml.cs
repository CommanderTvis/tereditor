using System.Globalization;
using System.Text.Json.Nodes;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.CodeIntelligence.UI;

/// <summary>
/// Settings › Code Intelligence: the limits (idle timeout, memory budget, count caps); what is running now is shown in the Code Intelligence tool window, saved as the host setting <c>language-limits</c> and refreshed from its broadcast.
/// </summary>
internal sealed partial class LanguageLimitsView : UserControl
{
    private static readonly string[] JvmServers = ["intellij", "kotlin", "scala"];
    private readonly Func<CancellationToken, Task<LanguageRunningState>> running;
    private readonly Func<string> current;
    private readonly Func<string, Task> save;
    private CancellationTokenSource? lifetime;
    private JsonObject defaults = new();

    internal LanguageLimitsView(Func<CancellationToken, Task<LanguageRunningState>> running, Func<string> current, Func<string, Task> save)
    {
        this.running = running; this.current = current; this.save = save;
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) =>
        {
            lifetime = new();
            _ = ReadAsync(lifetime.Token);
        };
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    /// <summary>Shows the saved limits again, after this or another client changed them.</summary>
    internal void Refresh() => ShowLimits();

    private async Task ReadAsync(CancellationToken token)
    {
        try
        {
            var result = await Task.Run(async () => await running(token), token);
            if (token.IsCancellationRequested) return;
            Show(result);
            ShowLimits();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Control<TextBlock>("LimitsSummary").Text = "Memory budget unavailable: " + error.Message; }
    }

    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private static string Mib(long bytes) => (bytes / (1024 * 1024)).ToString("N0", CultureInfo.InvariantCulture) + Units.NoBreak + "MiB";

    private void Show(LanguageRunningState result)
    {
        defaults = new JsonObject(result.DefaultCaps.Select(cap => KeyValuePair.Create(cap.Key, (JsonNode?)cap.Value)));
        var budget = result.BudgetBytes;
        var physical = result.PhysicalBytes;
        Control<OptionalNumberRow>("LimitsBudget").ShowWhenOff((int)Math.Min(budget / (1024 * 1024), 1_048_576));
        Control<TextBlock>("LimitsSummary").Text = $"Memory budget now: {Mib(budget)}" +
            (result.AutomaticBudget ? $" (automatic: 30% of {Mib(physical)} physical memory, 3–24 GiB)" : "") +
            (result.Pressure ? " · the system reports memory pressure" : "");
    }

    private void ShowLimits()
    {
        JsonObject saved;
        try { saved = JsonNode.Parse(current() is { Length: > 0 } json ? json : "{}") as JsonObject ?? new(); }
        catch (System.Text.Json.JsonException) { saved = new(); }
        var caps = saved["caps"] as JsonObject ?? new();
        int Cap(string id, int fallback) => caps[id]?.GetValue<int>() ?? defaults[id]?.GetValue<int>() ?? fallback;
        // The host stores 0 for "never" and "automatic"; here that is an unchecked box. Off, the idle box shows the default
        // and the budget box the budget in force (set by Show, once the host has told it).
        static int? On(JsonNode? node) => node?.GetValue<int>() is > 0 and var value ? value : null;
        Control<OptionalNumberRow>("LimitsIdle").Show(saved["idleMinutes"] is null ? 10 : On(saved["idleMinutes"]), 10);
        var budget = Control<OptionalNumberRow>("LimitsBudget");
        budget.Show(On(saved["memoryBudgetMb"]), budget.Number ?? 4096);
        Control<TextBox>("LimitsCSharp").Text = Cap("csharp", 4).ToString(CultureInfo.InvariantCulture);
        Control<TextBox>("LimitsJvm").Text = Cap("kotlin", 2).ToString(CultureInfo.InvariantCulture);
        Control<TextBox>("LimitsOther").Text = Cap("*", 6).ToString(CultureInfo.InvariantCulture);
    }

    private async void SaveClicked(object? sender, RoutedEventArgs e)
    {
        var message = Control<TextBlock>("LimitsMessage");
        var idleRow = Control<OptionalNumberRow>("LimitsIdle");
        var budgetRow = Control<OptionalNumberRow>("LimitsBudget");
        static int? Number(string? text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
        if (idleRow.IsOn && idleRow.Number is null || budgetRow.IsOn && budgetRow.Number is null ||
            Number(Control<TextBox>("LimitsCSharp").Text) is not { } csharp || Number(Control<TextBox>("LimitsJvm").Text) is not { } jvm || Number(Control<TextBox>("LimitsOther").Text) is not { } other)
        {
            message.Foreground = Ui.Danger; message.Text = "Enter whole numbers.";
            return;
        }
        var caps = new JsonObject { ["csharp"] = csharp, ["*"] = other };
        foreach (var id in JvmServers) caps[id] = jvm;
        var json = new JsonObject { ["idleMinutes"] = idleRow.Value ?? 0, ["memoryBudgetMb"] = budgetRow.Value ?? 0, ["caps"] = caps }.ToJsonString();
        var button = Control<Button>("LimitsSave");
        button.IsEnabled = false;
        try
        {
            await Task.Run(() => save(json));
            message.Foreground = Ui.Muted; message.Text = "Saved. It applies to running servers now.";
        }
        catch (Exception error) { message.Foreground = Ui.Danger; message.Text = error.Message; }
        finally { button.IsEnabled = true; }
    }
}