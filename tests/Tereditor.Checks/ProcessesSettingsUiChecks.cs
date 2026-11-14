using System.Text.Json;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Tereditor.Checks.E2E;
using Tereditor.Host.Core;

using Switch = Tereditor.Plugins.UI.Kit.Switch;

namespace Tereditor.Checks;

/// <summary>Settings › Processes in a headless workbench: the memory guard's switch, limits and protected programs read and write the plugin's settings.</summary>
internal static class ProcessesSettingsUiChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static T Find<T>(Control scope, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs(); (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return scope.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
    }

    private static JsonElement Stored(HostStateStore state) => state.Current.PluginSettings.GetValueOrDefault("processes");

    internal static void Run(string root)
    {
        var workspace = Path.Combine(root, "processes-settings-ui", "workspace");
        var profile = Path.Combine(root, "processes-settings-ui", "profile");
        Directory.CreateDirectory(workspace);
        using var app = new E2eWorkspace(workspace, profileRoot: profile);
        E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
        var window = app.Window;
        var state = app.State!;
        window.ShowSettings("Plugins");
        E2eWorkspace.Until(() => window.OpenSettingsViews.Any());
        var settings = window.OpenSettingsViews.Single();
        E2eWorkspace.Until(() => settings.GetLogicalDescendants().OfType<Control>().Any(item => item.Name == "Settings_plugin_processes_processes"));
        settings.ShowSection("plugin:processes:processes");
        E2eWorkspace.Until(() => settings.GetLogicalDescendants().OfType<Control>().Any(item => item.Name == "SettingsProcesses" && item.IsEffectivelyVisible));

        var guard = Find<Switch>(settings, "ProcessesMemoryGuard");
        var limits = Find<StackPanel>(settings, "ProcessesMemoryLimits");
        Require(guard.IsChecked == true && limits.IsEffectivelyVisible, "The memory guard is on by default, with its limits shown.");
        guard.Request(false);
        E2eWorkspace.Until(() => Stored(state) is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("memoryGuard", out var on) && !on.GetBoolean());
        E2eWorkspace.Until(() => !limits.IsVisible);
        guard.Request(true);
        E2eWorkspace.Until(() => limits.IsVisible);

        var perTerminal = Find<ComboBox>(settings, "ProcessesMemoryPerTerminal");
        perTerminal.SelectedIndex = 2;
        E2eWorkspace.Until(() => Stored(state).TryGetProperty("perTerminalMb", out var megabytes) && megabytes.GetInt32() == 4096);
        var total = Find<ComboBox>(settings, "ProcessesMemoryTotal");
        total.SelectedIndex = 1;
        E2eWorkspace.Until(() => Stored(state).TryGetProperty("totalMb", out var megabytes) && megabytes.GetInt32() == 8192);

        var protect = Find<TextBox>(settings, "ProcessesMemoryProtect");
        protect.Focus();
        E2eWorkspace.Until(() => protect.IsFocused);
        protect.Text = "postgres, my-database";
        guard.Focus();
        E2eWorkspace.Until(() => !protect.IsFocused);
        E2eWorkspace.Until(() => Stored(state).TryGetProperty("protect", out var names) && names.GetArrayLength() == 2 && names[1].GetString() == "my-database");

        // What the host holds is what the controls show again after a reopen.
        settings.Close();
        window.ShowSettings("Plugins");
        E2eWorkspace.Until(() => window.OpenSettingsViews.Any());
        settings = window.OpenSettingsViews.Single();
        E2eWorkspace.Until(() => settings.GetLogicalDescendants().OfType<Control>().Any(item => item.Name == "Settings_plugin_processes_processes"));
        settings.ShowSection("plugin:processes:processes");
        E2eWorkspace.Until(() => settings.GetLogicalDescendants().OfType<Control>().Any(item => item.Name == "SettingsProcesses" && item.IsEffectivelyVisible));
        Require(Find<ComboBox>(settings, "ProcessesMemoryPerTerminal").SelectedIndex == 2 && Find<TextBox>(settings, "ProcessesMemoryProtect").Text == "postgres, my-database", "The controls show the stored settings.");
        Console.WriteLine("PASS processes settings: the memory guard's switch, limits and protected programs are the plugin's settings, shown and saved from Settings › Processes");
    }
}