using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Pi.UI;

/// <summary>
/// The Pi pane of a workspace, shaped like Claude Code's configuration pane: Context (what a new session loads), Settings (settings
/// keys and packages), Capabilities (the open chat's commands) and Account (version, early-support badge and trust). It reads through the host and
/// changes only packages, through `pi install` and `pi remove`.
/// </summary>
internal sealed class PiConfigPanel : DockPanel
{
    private enum Surface { Context, Settings, Capabilities, Account }

    private readonly IPluginUIContext context;
    private readonly StackPanel scopeBlock = new() { Name = "PiConfigScope", Margin = new Thickness(8, 0, 8, 4) };
    private readonly StackPanel notices = new();
    private readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel switcher = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private string workspaceId;
    private Surface surface = Surface.Context;
    private PiConfigSnapshot? snapshot;
    private PiStatus? status;
    private string error = "", result = "", packageSource = "";
    private bool resultOk, packageLocal;
    private int generation;

    public PiConfigPanel(IPluginUIContext context, string workspaceId)
    {
        this.context = context;
        this.workspaceId = workspaceId;
        Name = "PiConfigPanel";
        Background = Ui.Sidebar;
        var header = new ScrollViewer { Content = switcher, Margin = new Thickness(8, 4), HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SetDock(header, Dock.Top);
        Children.Add(header);
        var hint = Ui.Text("Right-click a row to open its file", Ui.Hint, 12);
        scopeBlock.Children.Add(Ui.Text("Showing what a new Pi session here would load", Ui.Muted, 13));
        scopeBlock.Children.Add(hint);
        SetDock(scopeBlock, Dock.Top);
        Children.Add(scopeBlock);
        SetDock(notices, Dock.Top);
        Children.Add(notices);
        Children.Add(new ScrollViewer { Name = "PiConfigBody", Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        RenderSwitcher();
        AttachedToVisualTree += (_, _) => Load();
    }

    /// <summary>Points the pane at another workspace: the old snapshot and any reply still in flight for it are dropped.</summary>
    internal bool Retarget(string next)
    {
        if (next == workspaceId) return true;
        workspaceId = next;
        snapshot = null; error = ""; result = "";
        Load();
        return true;
    }

    private void Load()
    {
        var mine = ++generation;
        var id = workspaceId;
        _ = Task.Run(async () =>
        {
            PiConfigSnapshot? read = null;
            PiStatus? version = null;
            string failure = "";
            try { read = await context.RequestAsync(PiContract.Config, new PiConfigParams(id)); }
            catch (Exception failed) when (failed is not OperationCanceledException) { failure = "Pi configuration could not be read: " + failed.Message; }
            try { version = await context.RequestAsync(PiContract.Status, new PiNoParams()); }
            catch (Exception failed) when (failed is not OperationCanceledException) { }
            Dispatcher.UIThread.Post(() =>
            {
                if (mine != generation) return;
                snapshot = read ?? snapshot;
                status = version ?? status;
                error = failure;
                Render();
            });
        });
    }

    // Installs or removes a package, then reads the workspace again so the rows show the change.
    private void ChangePackage(bool install, string source, bool local)
    {
        var mine = ++generation;
        var id = workspaceId;
        _ = Task.Run(async () =>
        {
            PiPackageResult? answer = null;
            string failure = "";
            try { answer = await context.RequestAsync(install ? PiContract.PackageInstall : PiContract.PackageRemove, new PiPackageParams(id, source, local)); }
            catch (Exception failed) when (failed is not OperationCanceledException) { failure = failed.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                if (mine != generation) return;
                resultOk = answer?.Ok == true;
                result = failure.Length > 0 ? failure
                    : resultOk ? (install ? $"Installed {source}. Restart the chat to load it." : $"Removed {source}. Restart the chat to unload it.")
                    : answer?.Output is { Length: > 0 } output ? output : "The change failed.";
                Load();
            });
        });
    }

    private void RenderSwitcher()
    {
        switcher.Children.Clear();
        foreach (var (value, label) in new[] { (Surface.Context, "Context"), (Surface.Settings, "Settings"), (Surface.Capabilities, "Capabilities"), (Surface.Account, "Account") })
        {
            var segment = Ui.Segment("PiSurface_" + value.ToString().ToLowerInvariant(), label);
            segment.IsChecked = surface == value;
            segment.Click += (_, _) =>
            {
                surface = value;
                RenderSwitcher();
                Render();
            };
            switcher.Children.Add(segment);
        }
    }

    private void Render()
    {
        notices.Children.Clear();
        if (error.Length > 0) notices.Children.Add(Notice(error, Ui.Danger, "PiConfigError"));
        if (result.Length > 0) notices.Children.Add(Notice(result, resultOk ? Ui.Muted : Ui.Danger, "PiConfigResult"));
        scopeBlock.IsVisible = surface == Surface.Context;
        body.Content = snapshot is null
            ? Padded(Ui.Text(error.Length > 0 ? "Pi configuration could not be read." : "Reading configuration…", Ui.Muted, 13))
            : surface switch
            {
                Surface.Context => ContextSurface(snapshot),
                Surface.Settings => SettingsSurface(snapshot),
                Surface.Capabilities => CapabilitiesSurface(snapshot),
                _ => AccountSurface(snapshot, status)
            };
    }

    private Control ContextSurface(PiConfigSnapshot current)
    {
        var list = new StackPanel();
        list.Children.Add(TrustLine(current));
        list.Children.Add(Section("INSTRUCTIONS", Items(current, PiConfigKinds.Context).Select(ItemRow).ToList(), "No instruction files reach this workspace."));
        list.Children.Add(Section("EXTENSIONS", Items(current, PiConfigKinds.Extension).Select(ItemRow).ToList(), "No extensions are found."));
        list.Children.Add(Section("SKILLS", Items(current, PiConfigKinds.Skill).Select(ItemRow).ToList(), "No skills are found."));
        list.Children.Add(Section("PROMPT TEMPLATES", Items(current, PiConfigKinds.Prompt).Select(ItemRow).ToList(), "No prompt templates are found."));
        list.Children.Add(Section("MCP SERVERS", Items(current, PiConfigKinds.Mcp).Select(ItemRow).ToList(), "No MCP servers are configured."));
        return list;
    }

    // Settings groups are 16 apart with 4 inside a group, as in Claude Code's Settings tab.
    internal Control SettingsSurface(PiConfigSnapshot current)
    {
        var list = new StackPanel { Spacing = 16 };
        var packages = Items(current, PiConfigKinds.Package).Select(PackageRow).ToList();
        var block = new StackPanel { Name = "PiPackagesBlock", Spacing = 4 };
        block.Children.Add(Section("PACKAGES", packages, "No packages are installed."));
        var input = new TextBox { Name = "PiPackageSource", PlaceholderText = "npm:@scope/package or git:github.com/user/repo", Text = packageSource, MinWidth = 240 };
        input.TextChanged += (_, _) => packageSource = input.Text ?? "";
        var local = new CheckBox { Name = "PiPackageLocal", Content = "Project settings", IsChecked = packageLocal };
        local.IsCheckedChanged += (_, _) => packageLocal = local.IsChecked == true;
        var entry = new WrapPanel { ItemSpacing = 12, LineSpacing = 8 };
        entry.Children.Add(input);
        entry.Children.Add(local);
        var install = ScopedSetting.RowAction("PiPackageInstall", "Install", () => ChangePackage(true, packageSource.Trim(), packageLocal));
        install.HorizontalAlignment = HorizontalAlignment.Left;
        var hint = Ui.Text("Installs with pi install. Without Project settings, the package goes to USER settings. Restart the chat after a change.", Ui.Hint, 12);
        hint.Name = "PiPackageHint";
        hint.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        hint.TextTrimming = Avalonia.Media.TextTrimming.None;
        var actions = new StackPanel { Spacing = 4 };
        actions.Children.Add(install);
        actions.Children.Add(hint);
        var form = new StackPanel { Margin = new Thickness(8, 4), Spacing = 12 };
        form.Children.Add(entry);
        form.Children.Add(actions);
        block.Children.Add(form);
        list.Children.Add(block);
        var keys = current.Settings.Select(setting =>
        {
            var line = new DockPanel { Name = "PiConfigSetting", Margin = new Thickness(8, 4) };
            var value = Ui.Text(setting.Loaded ? setting.Value : setting.Value + " · not loaded until the project is trusted", Ui.Hint, 11);
            value.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            var column = new StackPanel();
            var title = new WrapPanel();
            title.Children.Add(Ui.Text(setting.Key, Ui.TextBrush, 13));
            title.Children.Add(ScopedSetting.ScopeChip(ScopeLabel(setting.Scope), "PiScopeChip"));
            column.Children.Add(title);
            column.Children.Add(value);
            line.Children.Add(column);
            return (Control)line;
        }).ToList();
        list.Children.Add(Section("SETTINGS", keys, "No settings are set."));
        return list;
    }

    private Control CapabilitiesSurface(PiConfigSnapshot current)
    {
        if (!current.ChatOpen) return Padded(Ui.Text("Open a Pi chat in this workspace to list its commands.", Ui.Muted, 13));
        var rows = current.Commands.OrderBy(command => command.Source, StringComparer.Ordinal).ThenBy(command => command.Name, StringComparer.Ordinal).Select(command =>
        {
            var line = new DockPanel { Name = "PiCommand", Margin = new Thickness(8, 4) };
            var column = new StackPanel();
            var title = new WrapPanel();
            title.Children.Add(Ui.Text("/" + command.Name, Ui.TextBrush, 13));
            if (command.Source.Length > 0) title.Children.Add(ScopedSetting.ScopeChip(command.Source, "PiSourceChip"));
            column.Children.Add(title);
            if (command.Description.Length > 0) column.Children.Add(Ui.Text(command.Description, Ui.Hint, 12));
            line.Children.Add(column);
            return (Control)line;
        }).ToList();
        return Section("COMMANDS", rows, "This Pi reports no commands.");
    }

    // The chat header shows the model and thinking level, so this pane leaves them out.
    internal static Control AccountSurface(PiConfigSnapshot current, PiStatus? status)
    {
        var list = new StackPanel { Margin = new Thickness(8, 4), Spacing = 4 };
        var badge = new Border { Background = Ui.WarningWash, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 0), Child = Ui.Text("Early support", Ui.Warning, 11), HorizontalAlignment = HorizontalAlignment.Left };
        list.Children.Add(badge);
        var version = status?.Installed == true ? (status.Version is { Length: > 0 } found ? "Version " + found : "Version unknown") : status?.Detail ?? "Reading the version…";
        var versionLine = Ui.Text(version, Ui.Hint, 12);
        versionLine.Name = "PiVersion";
        list.Children.Add(versionLine);
        var trust = Ui.Text("Trust: " + current.Trust + " (set with /trust in Pi)", Ui.Hint, 12);
        trust.Name = "PiAccountTrust";
        list.Children.Add(trust);
        return list;
    }

    private Control TrustLine(PiConfigSnapshot current)
    {
        var line = new DockPanel { Name = "PiTrust", Margin = new Thickness(8, 4) };
        var value = Ui.Text(current.Trust, Ui.Muted, 12);
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(value);
        line.Children.Add(Ui.Text("Project trust", Ui.TextBrush, 13));
        var column = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        column.Children.Add(line);
        column.Children.Add(Ui.Text("Pi loads project extensions, skills, prompts, MCP servers and settings only after you trust the project. Set this with /trust in a Pi chat; tereditor only shows it.", Ui.Hint, 12));
        return column;
    }

    private Control ItemRow(PiConfigItem item)
    {
        var row = new DockPanel { Name = "PiConfigItem", Tag = item.Kind, Margin = new Thickness(8, 4) };
        var icon = Ui.Icon(KindIcon(item.Kind), item.Loaded ? Ui.Hint : Ui.Muted, 14);
        icon.Margin = new Thickness(0, 2, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var column = new StackPanel();
        var title = new WrapPanel();
        title.Children.Add(Ui.Text(item.Name, item.Loaded ? Ui.TextBrush : Ui.Muted, 13));
        title.Children.Add(ScopedSetting.ScopeChip(ScopeLabel(item.Scope), "PiScopeChip"));
        column.Children.Add(title);
        column.Children.Add(ScopedSetting.SourcePath(item.Path, OpenFile));
        if (item.Detail.Length > 0) column.Children.Add(Ui.Text(item.Detail, Ui.Hint, 12));
        row.Children.Add(column);
        ToolTip.SetTip(row, "Right-click to open the file");
        row.PointerPressed += (_, pointer) => { if (pointer.GetCurrentPoint(row).Properties.IsRightButtonPressed) OpenFile(item.Path); };
        return row;
    }

    private Control PackageRow(PiConfigItem item)
    {
        var line = new DockPanel { Name = "PiPackage", Tag = item.Scope, Margin = new Thickness(8, 4) };
        var remove = ScopedSetting.RowAction("PiPackageRemove", "Remove", () => ChangePackage(false, item.Name, item.Scope == PiScopes.Project), danger: true);
        DockPanel.SetDock(remove, Dock.Right);
        line.Children.Add(remove);
        var column = new StackPanel();
        var title = new WrapPanel();
        title.Children.Add(Ui.Text(item.Name, Ui.TextBrush, 13));
        title.Children.Add(ScopedSetting.ScopeChip(ScopeLabel(item.Scope), "PiScopeChip"));
        column.Children.Add(title);
        if (item.Detail.Length > 0) column.Children.Add(Ui.Text(item.Detail, Ui.Hint, 12));
        line.Children.Add(column);
        return line;
    }

    private static Control Section(string title, IReadOnlyList<Control> rows, string empty)
    {
        var section = new StackPanel();
        section.Children.Add(Padded(Ui.Text(title, Ui.Hint, 10)));
        if (rows.Count == 0) section.Children.Add(Padded(Ui.Text(empty, Ui.Muted, 13)));
        foreach (var row in rows) section.Children.Add(row);
        return section;
    }

    private static IReadOnlyList<PiConfigItem> Items(PiConfigSnapshot current, string kind) => current.Items.Where(item => item.Kind == kind).ToArray();

    private static string ScopeLabel(string scope) => scope == PiScopes.Project ? "PROJECT" : "USER";

    private static string KindIcon(string kind) => kind switch
    {
        PiConfigKinds.Extension => "plug",
        PiConfigKinds.Skill => "sparkling",
        PiConfigKinds.Prompt => "chat",
        PiConfigKinds.Mcp => "server",
        PiConfigKinds.Package => "box",
        _ => "fileText"
    };

    private void OpenFile(string path) =>
        _ = context.Editors.OpenAsync(workspaceId, path, null).AsTask().ContinueWith(_ => { }, TaskScheduler.Default);

    private static TextBlock Notice(string text, Avalonia.Media.IBrush color, string name)
    {
        var block = Ui.Text(text, color, 13);
        block.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        block.Margin = new Thickness(8, 4);
        block.Name = name;
        return block;
    }

    private static Control Padded(Control child)
    {
        child.Margin = new Thickness(8, 4);
        return child;
    }
}