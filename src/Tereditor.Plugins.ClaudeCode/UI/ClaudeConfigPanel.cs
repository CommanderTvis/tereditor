using System.Globalization;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.ClaudeCode.UI;

/// <summary>
/// The Claude Code side tool: what a Claude session in this workspace loads (context, settings, capabilities) with the
/// provenance of each part, and the account. It describes one terminal session, the first shown tab running Claude, else
/// what a new session at the workspace root would load. Every change is composed, scoped, reviewed as a diff, then written.
/// </summary>
internal sealed class ClaudeConfigPanel : DockPanel, IClaudeCapabilityHost
{
    private enum Surface { Context, Settings, Capabilities, Account }

    private readonly IPluginUIContext context;
    private readonly ClaudeContextUsageView usageView = new();
    private ClaudeContextUsage? contextUsage;
    private int usageGeneration;
    private bool usagePending;
    private DateTimeOffset? requestedAt;
    private readonly ClaudeAutoRefresh auto;
    private DispatcherTimer? minute;
    private IDisposable? usageChannel;
    private string memoryKey = "";
    private readonly ClaudeGlyph glyph;
    private string workspaceId;
    private readonly StackPanel scopeBlock = new() { Name = "ClaudeConfigScope", Margin = new Thickness(8, 0, 8, 4) };
    private readonly StackPanel notices = new();
    private readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel switcher = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private Surface surface = Surface.Context;
    private ClaudeConfigSnapshot? snapshot;
    private IReadOnlyList<ClaudeCapability> reached = [];
    private string? error;
    private ClaudeAccountSurface? account;
    private int generation;
    private TerminalTabInfo? session;
    private TerminalTabInfo? activeTab;
    private string settingsQuery = "";
    private bool showDocumented;
    private IDisposable? watch;
    private string snapshotKey = "", reachedKey = "";
    private (Surface, string, string, string?, string, string)? rendered;
    private (Surface, string?, string?, string?, string?)? renderedScope;

    public ClaudeConfigPanel(IPluginUIContext context, ClaudeGlyph glyph, string workspaceId)
    {
        this.context = context; this.glyph = glyph; this.workspaceId = workspaceId;
        usageView.RetryRequested += () => LoadUsage(refresh: true);
        Name = "ClaudeConfigPanel";
        Background = Ui.Sidebar;
        var header = new DockPanel { Margin = new Thickness(8, 4) };
        auto = new ClaudeAutoRefresh(() => DateTimeOffset.UtcNow, () => TopLevel.GetTopLevel(this) is Window { IsActive: true, WindowState: not WindowState.Minimized } && IsEffectivelyVisible,
            () => usagePending || contextUsage?.Running == true || account?.Busy == true, LastRead, Refresh);
        var refresh = Ui.IconButton("refresh", "Refresh usage and context (also runs hourly)", Refresh);
        refresh.Name = "ClaudeConfigRefresh";
        refresh.Width = refresh.Height = 24;
        refresh.Padding = new Thickness(4);
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(new ScrollViewer { Content = switcher, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        SetDock(header, Dock.Top);
        Children.Add(header);
        SetDock(scopeBlock, Dock.Top);
        Children.Add(scopeBlock);
        SetDock(notices, Dock.Top);
        Children.Add(notices);
        Children.Add(new ScrollViewer { Name = "ClaudeConfigBody", Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        RenderSwitcher();
        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) =>
        {
            watch?.Dispose(); watch = null;
            usageChannel?.Dispose(); usageChannel = null;
            minute?.Stop(); minute = null;
            Ui.ThemeChanged -= ThemeChanged;
        };
    }

    /// <summary>
    /// Points this pane at another workspace (the side tool's <c>Retarget</c>): the snapshot, usage, error, search text, expanded
    /// rows and the surface (back to Context) of the old workspace are dropped, reads and usage replies still in flight for it
    /// are rejected, and the new workspace is read. The account surface is not workspace data and stays.
    /// </summary>
    internal bool Retarget(string next)
    {
        if (next == workspaceId) return true;
        workspaceId = next;
        generation++; usageGeneration++;
        usagePending = false; requestedAt = null;
        snapshot = null; reached = []; error = null; contextUsage = null; session = activeTab = null;
        snapshotKey = reachedKey = memoryKey = ""; settingsQuery = ""; surface = Surface.Context;
        expandedRows.Clear();
        collapsedGroups.Clear();
        rendered = null; renderedScope = null;
        RenderSwitcher();
        ShowUsage();
        RenderScope();
        Render();
        // The host watch compares against the old workspace's tab, so it is registered again for the new one.
        watch?.Dispose(); watch = null;
        if (TopLevel.GetTopLevel(this) is not null) Start();
        return true;
    }

    // The session this pane describes: among the terminals the workspace shows, the first running Claude.
    private (TerminalTabInfo? Session, TerminalTabInfo? Active) Shown(PluginHostProjection host)
    {
        var tabs = host.Terminals.GetValueOrDefault(workspaceId) ?? [];
        var shown = (host.ShownTerminalTabKeys.GetValueOrDefault(workspaceId) ?? []).Select(key => tabs.FirstOrDefault(tab => tab.TabKey == key)).OfType<TerminalTabInfo>().ToArray();
        return (shown.FirstOrDefault(tab => tab.Agent?.Kind == "claude"), shown.FirstOrDefault());
    }

    private void Start()
    {
        // The host measures in the background and says when a baseline for this workspace is ready.
        usageChannel ??= context.Subscribe(ClaudeCodeContract.ContextUsage, change =>
        {
            if (change.WorkspaceId == workspaceId) LoadUsage(refresh: false);
        });
        Ui.ThemeChanged -= ThemeChanged;
        Ui.ThemeChanged += ThemeChanged;
        if (minute is null)
        {
            minute = new DispatcherTimer { Interval = ClaudeAutoRefresh.TickEvery };
            minute.Tick += (_, _) => { usageView.Retick(); auto.Tick(); };
            minute.Start();
        }
        (session, activeTab) = Shown(context.Host());
        watch ??= context.WatchHost(host =>
        {
            var (found, active) = Shown(host);
            return (found?.TabKey, found?.Title, found?.Agent?.Command, active?.TabKey, active?.Title);
        }, (_, previous) =>
        {
            var previousTab = previous.Item1;
            var previousCommand = previous.Item3;
            (session, activeTab) = Shown(context.Host());
            if (session?.TabKey != previousTab || session?.Agent?.Command != previousCommand) { Load(); LoadUsage(refresh: false); }
            RenderScope();
        });
        RenderScope();
        Load();
        LoadUsage(refresh: false);
    }

    private void ThemeChanged()
    {
        rendered = null;
        Render();
        ShowUsage();
    }

    private void ShowUsage() => usageView.Show(contextUsage);

    // The button and the hourly tick are the same refresh: on the account surface it also asks Claude Code for fresh usage numbers.
    private void Refresh()
    {
        if (surface == Surface.Account) account?.Read(refresh: true);
        Load();
        if (surface == Surface.Context) LoadUsage(refresh: true);
        requestedAt = DateTimeOffset.UtcNow;
    }

    // The newer of the baseline's own stamp and our last request, so a failed refresh is not retried every minute.
    private DateTimeOffset? LastRead()
    {
        DateTimeOffset? measured = contextUsage?.MeasuredAt is { } stamp && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        return measured is null ? requestedAt : requestedAt is { } asked && asked > measured ? asked : measured;
    }

    // The host answers at once with what it has cached and measures in the background when that is missing or stale.
    private void LoadUsage(bool refresh)
    {
        var mine = ++usageGeneration;
        usagePending = true;
        var tabKey = session?.TabKey;
        _ = Task.Run(async () =>
        {
            ClaudeContextUsage result;
            try { result = await context.RequestAsync(ClaudeCodeContract.ContextUsageGet, new ContextUsageParams(workspaceId) { TabKey = tabKey, Refresh = refresh }); }
            catch (Exception failure) when (failure is not OperationCanceledException) { result = new(false) { Error = ClaudeParts.ErrorText(failure), Breakdown = contextUsage?.Breakdown }; }
            var key = PluginJson.Serialize(result.Breakdown);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (mine != usageGeneration) return;
                usagePending = false;
                contextUsage = result;
                ShowUsage();
                if (!refresh) auto.Tick();
                if (key != memoryKey)
                {
                    memoryKey = key;
                    Render();
                }
            });
        });
    }

    // Every read is issued; a reply is applied only if no later read was issued after it, which is ordering, not a gate.
    private void Load()
    {
        var mine = ++generation;
        var tabKey = session?.TabKey;
        _ = Task.Run(async () =>
        {
            ClaudeConfigSnapshot result;
            try { result = await context.RequestAsync(ClaudeCodeContract.ConfigGet, new WorkspaceParams(workspaceId) { TabKey = tabKey }); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (mine != generation) return;
                    error = ClaudeParts.ErrorText(failure);
                    Render();
                });
                return;
            }
            var key = PluginJson.Serialize(result);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (mine != generation) return;
                snapshot = result;
                snapshotKey = key;
                error = null;
                RenderScope();
                Render();
            });
            // What Claude itself reaches beyond the files costs a health check, so it is asked after and never holds the pane.
            try
            {
                var listed = await context.RequestAsync(ClaudeCodeContract.McpList, new WorkspaceParams(workspaceId) { TabKey = tabKey });
                var capabilitiesKey = PluginJson.Serialize(listed.Capabilities);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (mine != generation) return;
                    reached = listed.Capabilities;
                    reachedKey = capabilitiesKey;
                    if (surface == Surface.Capabilities) Render();
                });
            }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
        });
    }

    private void Reload() => Load();

    private void RenderSwitcher()
    {
        if (switcher.Children.Count > 0)
        {
            foreach (var segment in switcher.Children.OfType<ToggleButton>()) segment.IsChecked = Equals(segment.Tag, surface);
            return;
        }
        switcher.Children.Clear();
        foreach (var (value, label) in new[] { (Surface.Context, "Context"), (Surface.Settings, "Settings"), (Surface.Capabilities, "Capabilities"), (Surface.Account, "Account") })
        {
            var segment = Ui.Segment("ClaudeSurface_" + value.ToString().ToLowerInvariant(), label);
            segment.Tag = value;
            segment.IsChecked = surface == value;
            segment.Click += (_, _) =>
            {
                surface = value;
                RenderSwitcher();
                RenderScope();
                Render();
            };
            switcher.Children.Add(segment);
        }
    }

    private void RenderScope()
    {
        var key = (surface, session?.TabKey, session?.Title, activeTab?.Title, snapshot?.Root);
        if (renderedScope == key) return;
        renderedScope = key;
        scopeBlock.Children.Clear();
        scopeBlock.IsVisible = surface == Surface.Context;
        scopeBlock.Tag = session is not null ? "session" : "workspace";
        if (session is not null)
        {
            scopeBlock.Children.Add(Ui.Text($"Context of Claude in tab {TabNames.Quote(session.Title)}", Ui.Muted, 13));
            scopeBlock.Children.Add(new PathLine { Path = snapshot?.Root });
        }
        else
        {
            scopeBlock.Children.Add(Ui.Text(activeTab is not null ? $"No Claude in tab {TabNames.Quote(activeTab.Title)}" : "No terminal open", Ui.Muted, 13));
            scopeBlock.Children.Add(Ui.Text("Showing what a new session here would load", Ui.Hint, 12));
        }
    }

    private void OpenSource(string path, IReadOnlyList<string>? keyPath) =>
        _ = context.Editors.OpenAsync(workspaceId, path, keyPath is null ? null : new EditorOpenOptions { KeyPath = keyPath }).AsTask()
            .ContinueWith(_ => { }, TaskScheduler.Default);

    private async void Edit(PendingEdit pending)
    {
        if (ClaudeParts.Owner(this) is not { } owner) return;
        if (await ReviewDialogs.EditAsync(context, owner, workspaceId, session?.TabKey, pending)) Reload();
    }

    private void Render()
    {
        var key = (surface, snapshotKey, reachedKey, error, settingsQuery, memoryKey);
        if (rendered == key) return;
        rendered = key;
        notices.Children.Clear();
        if (error is not null)
        {
            var text = ClaudeParts.Wrapped(error, Ui.Danger, 13);
            text.Name = "ClaudeConfigError";
            text.Margin = new Thickness(8, 4);
            notices.Children.Add(text);
        }
        foreach (var problem in snapshot?.Problems ?? [])
        {
            var row = new DockPanel { Name = "ClaudeConfigProblem", Tag = problem.Severity, Margin = new Thickness(8, 4) };
            var icon = problem.Severity == ClaudeProblemSeverity.Warning ? Ui.Icon("alertWarning", Ui.Warning, 14) : Ui.Icon("alertInfo", Ui.Info, 14);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 4, 0);
            SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var column = new StackPanel();
            column.Children.Add(Ui.Text(problem.Title, Ui.TextBrush, 13));
            column.Children.Add(ClaudeParts.Wrapped(problem.Detail));
            if (problem.Path is { } path) column.Children.Add(ClaudeParts.SourceButton(path, null, OpenSource));
            row.Children.Add(column);
            notices.Children.Add(row);
        }
        body.Content = snapshot is null
            ? Padded(Ui.Text("Reading configuration…", Ui.Muted, 13))
            : surface switch
            {
                Surface.Context => ContextSurface(snapshot.Context),
                Surface.Settings => SettingsSurface(snapshot),
                Surface.Account => Account(),
                _ => new ClaudeCapabilitiesSurface(this, [.. snapshot.Capabilities, .. reached]).View
            };
    }

    private ClaudeAccountSurface Account()
    {
        if (account is not null) return account;
        account = new ClaudeAccountSurface(context) { Name = "ClaudeAccount", Spacing = 16, Margin = new Thickness(8) };
        account.Read(refresh: false);
        return account;
    }

    private static Control Padded(Control child)
    {
        child.Margin = new Thickness(8);
        return child;
    }

    private static Button Offer(string name, string title, string description, Action open)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(Ui.Text(title, Ui.TextBrush, 13));
        content.Children.Add(ClaudeParts.Wrapped(description));
        var button = new Button
        {
            Name = name,
            Content = content,
            Background = Brushes.Transparent,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => open();
        return button;
    }

    // The size column docks right and is measured first, so the left column only gets what is left: the name ellipsizes
    // within it and the badge wraps below the name instead of running under the size.
    private static Border SizeColumn(Control size)
    {
        var column = new Border { Name = "ClaudeContextSize", Child = size, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        SetDock(column, Dock.Right);
        return column;
    }

    private static WrapPanel Title(string label, bool denied = false)
    {
        var name = Ui.Text(label, denied ? Ui.Hint : Ui.TextBrush, 13);
        name.Name = "ClaudeContextName";
        if (denied) name.TextDecorations = TextDecorations.Strikethrough;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        return new WrapPanel { Name = "ClaudeContextTitle", Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 2, Children = { name } };
    }

    // Tokens as /context counts them; nothing else, since only tokens take up the window.
    // "1 token", "23.2k tokens", or "tok"/"toks" when the row is narrow.
    private static Control Measure(long tokens, IBrush color, string suffix = "") =>
        new TokenAmount(ClaudeContextUsageView.Tokens(tokens), tokens, color, suffix) { Name = "ClaudeContextTokens", VerticalAlignment = VerticalAlignment.Center };

    // Until a baseline exists there is no count to show.
    private static Control Unmeasured()
    {
        var pending = ClaudeParts.Code("…", Ui.Hint);
        ToolTip.SetTip(pending, "Measuring with claude -p /context");
        return pending;
    }

    private static Control NotReported()
    {
        var text = Ui.Text("—", Ui.Hint, 11);
        text.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(text, "/context does not report a size for each built-in tool");
        return text;
    }

    // A row the user turned off: said in words at the right, where the size would be, at the same 11px as the dash it replaces.
    private static Control Denied()
    {
        var text = Ui.Text("Denied", Ui.Danger, 11);
        text.Name = "ClaudeContextDenied";
        text.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(text, "Turned off in the Claude Code settings");
        return text;
    }

    private static Control NotLoaded()
    {
        var text = Ui.Text("not loaded", Ui.Hint, 11);
        text.Name = "ClaudeContextNotLoaded";
        text.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(text, "/context does not list this file for a fresh session here");
        return text;
    }

    // The built-in tools the tools reference names that a user can hide with a bare deny rule. EndConversation (cannot be
    // removed) and ToolSearch (loads the deferred MCP tools) are left out on purpose.
    internal static readonly string[] BuiltInTools =
    [
        "Agent", "AskUserQuestion", "Bash", "CronCreate", "CronDelete", "CronList", "Edit", "EnterPlanMode", "EnterWorktree", "Glob", "Grep",
        "ListMcpResourcesTool", "LSP", "Monitor", "NotebookEdit", "Read", "ReadMcpResourceTool", "Skill", "TodoWrite", "WebFetch", "WebSearch", "Workflow", "Write"
    ];

    /// <summary>One context-menu entry of a Context row: its label and the edit the review dialog then asks a scope for.</summary>
    internal sealed record RowAction(string Label, PendingEdit Edit);

    // Claude Code writes a server's name into tool names with every "." and space as "_".
    internal static string ServerToolPrefix(string server) => "mcp__" + server.Replace('.', '_').Replace(' ', '_');

    private const string RestartNote = "Sessions you start afterwards use this. A session that is already running may keep what it loaded.";

    /// <summary>
    /// What a Context row can turn off, from the documented switches only: a skill through <c>skillOverrides</c> (a plugin's
    /// skill through a <c>Skill(name)</c> deny rule, since overrides skip plugin skills), an MCP server or tool and a built-in
    /// tool through <c>permissions.deny</c>. Rows that cannot be turned off, like the system prompt, get none. A row that is
    /// already off offers the way back.
    /// </summary>
    internal static List<RowAction> RowActions(BaselineEntry entry, IReadOnlySet<string> denied, IReadOnlySet<string> skillsOff)
    {
        RowAction Toggle(string noun, string rule, string name, string detail)
        {
            var off = denied.Contains(rule);
            return off ? new($"Turn {noun} back on", new(new DenyToolEdit(rule, false), $"Turn \"{name}\" back on", detail))
                : new($"Turn off this {noun}", new(new DenyToolEdit(rule, true), $"Turn off \"{name}\"", detail));
        }
        if (entry.Key.StartsWith("skills/", StringComparison.Ordinal))
        {
            var name = entry.Key["skills/".Length..];
            // A plugin's skill file is read-only, but the skill can still be denied by rule.
            if (entry.Tip?.StartsWith("Plugin", StringComparison.OrdinalIgnoreCase) == true) return [Toggle("skill", $"Skill({name})", name, RestartNote)];
            return skillsOff.Contains(name)
                ? [new("Turn skill back on", new(new SkillEdit(name, true), $"Turn \"{name}\" back on", RestartNote))]
                : [new("Turn off this skill", new(new SkillEdit(name, false), $"Turn off \"{name}\"", RestartNote))];
        }
        if (entry.Key.StartsWith("mcp-tools/", StringComparison.Ordinal))
        {
            var rest = entry.Key["mcp-tools/".Length..];
            var slash = rest.IndexOf('/');
            return slash < 0 ? [Toggle("MCP server", ServerToolPrefix(rest) + "__*", rest, RestartNote)]
                : entry.Tip is { } tool ? [Toggle("MCP tool", tool, tool, RestartNote)] : [];
        }
        if (entry.Key.StartsWith("system-tools/", StringComparison.Ordinal))
            return [Toggle("tool", entry.Label, entry.Label, RestartNote)];
        return [];
    }

    /// <summary>
    /// The SKILL.md each skill name of the Context table opens, read from the snapshot's capabilities: a plugin's skills from
    /// the files its manifest declares (named <c>plugin:skill</c>), user and project skills from their folder. A skill with
    /// no file on disk, such as one compiled into Claude Code, is absent.
    /// </summary>
    internal static Dictionary<string, string> SkillFiles(IReadOnlyList<ClaudeCapability> capabilities)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in capabilities)
        {
            if (item.Kind == ClaudeCapabilityKind.Skill && item.Origin.Path is { } folder) files.TryAdd(item.Name, System.IO.Path.Combine(folder, "SKILL.md"));
            foreach (var part in item.Parts ?? [])
                if (part.Kind == ClaudePluginPartKind.Skill && part.Path is { } file) files.TryAdd(part.Name, file);
        }
        return files;
    }

    private IReadOnlySet<string> rowDenied = new HashSet<string>(), rowSkillsOff = new HashSet<string>();
    private Dictionary<string, string> rowSkillFiles = [];

    // Suffix follows the count, as " if loaded" on a server whose tools /context counts whether loaded or deferred.
    internal sealed record BaselineEntry(string Key, string Label, string Icon, long Tokens, IReadOnlyList<BaselineEntry> Children, string? Tip = null, string Suffix = "");

    // Rows the user opened, by key, so a re-render keeps them open.
    private readonly HashSet<string> expandedRows = [];

    // Marketplaces (and the group without one) the user folded in the capabilities surface; all start open.
    private readonly HashSet<string> collapsedGroups = [];

    // /context's categories that a fresh session pays for, besides the memory files listed above: deferred tools load
    // only when searched for, free space and the autocompact reserve are not content, and the one message is /context itself.
    internal static List<BaselineEntry> BaselineRows(ClaudeContextBreakdown breakdown, long? claudePrompt)
    {
        var rows = new List<BaselineEntry>();
        // The "MCP tools" category counts the tools loaded at start; /context's tool table lists every tool, deferred
        // ones included, so a server's subtotal is what all its tools would cost and can exceed the category.
        var deferredMcp = breakdown.Categories.FirstOrDefault(category => category.Name == "MCP tools (deferred)")?.Tokens ?? 0;
        BaselineEntry McpRow(long loaded) => new("mcp-tools", "MCP tools", "server", loaded,
            [.. breakdown.McpTools.GroupBy(tool => tool.Source).OrderByDescending(group => group.Sum(tool => tool.Tokens)).Select(server =>
                new BaselineEntry("mcp-tools/" + server.Key, server.Key, "server", server.Sum(tool => tool.Tokens),
                    [.. server.OrderByDescending(tool => tool.Tokens).Select(tool => new BaselineEntry("mcp-tools/" + server.Key + "/" + tool.Name,
                        tool.Name.StartsWith("mcp__" + server.Key + "__", StringComparison.Ordinal) ? tool.Name[(server.Key.Length + 7)..] : tool.Name,
                        "code", tool.Tokens, [], tool.Name))],
                    deferredMcp > 0 ? "What all of this server's tools cost if loaded. Deferred tools load only when Claude searches for them." : null,
                    deferredMcp > 0 ? " if loaded" : ""))],
            deferredMcp > 0 ? $"Tools loaded at start. Deferred tools take {ClaudeContextUsageView.Tokens(deferredMcp)} tokens more: they load only when Claude searches for them." : null);
        // With every MCP tool deferred /context prints no "MCP tools" category; the row still lists the servers, at 0 loaded.
        var anyLoaded = breakdown.Categories.Any(category => category.Name == "MCP tools");
        foreach (var category in breakdown.Categories)
        {
            var name = category.Name;
            if (name == "MCP tools (deferred)" && !anyLoaded)
            {
                rows.Add(McpRow(0));
                continue;
            }
            if (name is "Memory files" or "Messages" or "Free space" || name.EndsWith("(deferred)", StringComparison.Ordinal) ||
                name.StartsWith("Autocompact", StringComparison.Ordinal)) continue;
            rows.Add(name switch
            {
                "System prompt" => new("system-prompt", "Claude Code system prompt", "robot", Math.Max(0, claudePrompt ?? category.Tokens), [],
                    "Claude Code's own prompt; tereditor's appended instructions are counted on their own row"),
                "System tools" => new("system-tools", "Built-in tools", "terminal", category.Tokens,
                    [.. BuiltInTools.Select(tool => new BaselineEntry("system-tools/" + tool, tool, "terminal", -1, [], tool))],
                    "/context reports one total for these. Turning a tool off removes its definition from Claude's context."),
                "MCP tools" => McpRow(category.Tokens),
                "MCP server instructions" => new("mcp-instructions", "MCP server instructions", "book", category.Tokens, []),
                "Skills" => new("skills", "Skills", "sparkling", category.Tokens, SkillRows(breakdown.Skills)),
                _ => new(name, name, "box", category.Tokens, [])
            });
        }
        return rows;
    }

    // A plugin's skills are named "plugin:skill": they sit together under one row for the plugin, the rest (user and project skills) stay loose.
    // The order is by size, a plugin by what all its skills take.
    private static BaselineEntry[] SkillRows(IEnumerable<ClaudeContextItem> skills)
    {
        var entries = new List<BaselineEntry>();
        foreach (var plugin in skills.GroupBy(skill => skill.Name.Contains(':') ? skill.Name[..skill.Name.IndexOf(':')] : ""))
        {
            if (plugin.Key.Length == 0)
                entries.AddRange(plugin.Select(skill => new BaselineEntry("skills/" + skill.Name, skill.Name, "sparkling", skill.Tokens, [], skill.Source)));
            else
                entries.Add(new("skills-plugin/" + plugin.Key, plugin.Key, "puzzle", plugin.Sum(skill => skill.Tokens),
                    [.. plugin.OrderByDescending(skill => skill.Tokens).Select(skill => new BaselineEntry("skills/" + skill.Name, skill.Name[(plugin.Key.Length + 1)..], "sparkling", skill.Tokens, [], skill.Source))],
                    "Skills from the plugin " + plugin.Key));
        }
        return [.. entries.OrderByDescending(entry => entry.Tokens)];
    }

    /// <summary>Adds the row's context menu and the button that opens it. The button always takes its space, so the row never changes height.</summary>
    internal static void AttachRowActions(DockPanel line, IReadOnlyList<RowAction> actions, Action<PendingEdit> edit)
    {
        if (actions.Count == 0) return;
        {
            var menu = new ContextMenu { Name = "ClaudeContextRowMenu" };
            foreach (var action in actions) menu.Items.Add(Ui.Menu(action.Label, () => edit(action.Edit)));
            line.ContextMenu = menu;
            // Right-click is not something a row hints at: a small button shows while the pointer is over it and opens the same menu.
            var more = new Button { Name = "ClaudeContextRowMore", Classes = { "icon" }, Content = Ui.Icon("moreHorizontal", Ui.Muted, 14), Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
            // The button always takes its space and only fades in, so a row never changes height when the pointer enters it.
            void ShowMore(bool shown) { more.Opacity = shown ? 1 : 0; more.IsHitTestVisible = shown; }
            ToolTip.SetTip(more, actions[0].Label);
            more.Click += (_, _) => menu.Open(line); // Avalonia opens a context menu only on the control it is attached to
            // The dots sit right after the title, and a hover bubble hugs both. The bubble keeps the title's place (negative margin
            // against its padding) and adds no height.
            var title = line.Children[^1];
            SetDock(more, Dock.Right);
            DockPanel named;
            line.Children.Remove(title);
            if (title is DockPanel { Name: "ClaudeContextNamed" } existing) { named = existing; named.Children.Insert(1, more); }
            else
            {
                named = new DockPanel { Name = "ClaudeContextNamed", HorizontalAlignment = HorizontalAlignment.Left, Children = { more, title } };
            }
            named.HorizontalAlignment = HorizontalAlignment.Left;
            var bubble = new Border { Name = "ClaudeContextBubble", CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 2, 0), Margin = new Thickness(-4, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, Child = named };
            line.Children.Add(bubble);
            void Hover(bool shown) { ShowMore(shown); bubble.Background = shown ? Ui.Hover : Brushes.Transparent; }
            line.PointerEntered += (_, _) => Hover(true);
            line.PointerExited += (_, _) => Hover(more.IsPressed || menu.IsOpen);
            menu.Closed += (_, _) => Hover(line.IsPointerOver);
        }
    }

    // The same row as a file's: tokens docked right, a glyph, the name. A row with parts keeps its glyph, adds a
    // chevron after its name and opens them below it, indented a step.
    private void AddBaselineRow(Panel list, BaselineEntry entry, int depth)
    {
        var open = expandedRows.Contains(entry.Key);
        var actions = RowActions(entry, rowDenied, rowSkillsOff);
        var line = BaselineLine(entry, depth, open, denied: IsOff(actions));
        list.Children.Add(line);
        AttachRowActions(line, actions, Edit);
        if (entry.Key.StartsWith("skills/", StringComparison.Ordinal) && entry.Children.Count == 0 && rowSkillFiles.TryGetValue(entry.Key["skills/".Length..], out var skillFile))
        {
            line.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            ToolTip.SetTip(line, skillFile);
            line.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(line).Properties.IsLeftButtonPressed) return;
                OpenSource(skillFile, null);
                args.Handled = true;
            };
        }
        if (entry.Children.Count == 0) return;
        var parts = new StackPanel { Name = "ClaudeContextBaselineParts", Tag = entry.Key, IsVisible = open };
        foreach (var child in entry.Children) AddBaselineRow(parts, child, depth + 1);
        list.Children.Add(parts);
        line.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(line).Properties.IsLeftButtonPressed) return;
            var nowOpen = !parts.IsVisible;
            if (nowOpen) expandedRows.Add(entry.Key); else expandedRows.Remove(entry.Key);
            parts.IsVisible = nowOpen;
            line.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(path => path.Name == "ClaudeContextChevron").Data = Geometry.Parse(nowOpen ? "M 3,5 L 7,9 L 11,5" : "M 5,3 L 9,7 L 5,11");
            args.Handled = true;
        };
    }

    // One row's line. The name trims with an ellipsis (its tooltip has it whole) so it never runs under the chevron
    // or the count: the chevron docks right inside a left-aligned panel, so it follows a short name and holds its
    // place at the end of the name's room when the name is long.
    /// <summary>Whether the row's one action is the way back, so the row is turned off.</summary>
    internal static bool IsOff(IReadOnlyList<RowAction> actions) => actions.Count > 0 && actions[0].Edit.Edit is DenyToolEdit { Denied: false } or SkillEdit { Enabled: true };

    internal static DockPanel BaselineLine(BaselineEntry entry, int depth, bool open, bool denied = false)
    {
        var line = new DockPanel { Name = "ClaudeContextBaseline", Tag = entry.Key, Margin = new Thickness(8 + depth * 16, 4, 8, 4), Background = Brushes.Transparent };
        line.Children.Add(SizeColumn(denied ? Denied() : entry.Tokens < 0 ? NotReported() : Measure(entry.Tokens, Ui.Muted, entry.Suffix)));
        var glyph = Ui.Icon(entry.Icon, denied ? Ui.Muted : Ui.Hint, 14);
        glyph.Margin = new Thickness(0, 0, 8, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        SetDock(glyph, Dock.Left);
        line.Children.Add(glyph);
        var title = Title(entry.Label, denied);
        ToolTip.SetTip(title.Children[0], entry.Label);
        if (entry.Children.Count > 0)
        {
            var chevron = new Avalonia.Controls.Shapes.Path
            {
                Name = "ClaudeContextChevron",
                Data = Geometry.Parse(open ? "M 3,5 L 7,9 L 11,5" : "M 5,3 L 9,7 L 5,11"),
                Width = 14,
                Height = 14,
                Stroke = Ui.Muted,
                StrokeThickness = 1.5,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            line.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            SetDock(chevron, Dock.Right);
            line.Children.Add(new DockPanel { Name = "ClaudeContextNamed", HorizontalAlignment = HorizontalAlignment.Left, Children = { chevron, title } });
        }
        else line.Children.Add(title);
        if (entry.Tip is not null) ToolTip.SetTip(line, entry.Tip);
        return line;
    }

    private Control ContextSurface(IReadOnlyList<ClaudeContextLayer> layers)
    {
        (usageView.Parent as Panel)?.Children.Remove(usageView);
        ShowUsage();
        if (layers.Count == 0)
        {
            var empty = new StackPanel();
            empty.Children.Add(usageView);
            empty.Children.Add(Padded(Ui.Text("No instruction files reach this workspace.", Ui.Muted, 13)));
            return empty;
        }
        var list = new StackPanel();
        list.Children.Add(usageView);
        // /context's memory table, keyed by the host-normalised path; null until a baseline exists, so nothing says "not loaded" early.
        var measured = contextUsage?.Breakdown?.MemoryFiles;
        var memoryTokens = (measured ?? []).GroupBy(file => file.Name).ToDictionary(group => group.Key, group => group.First().Tokens);
        var hasProjectInstructions = layers.Any(layer => layer.Kind == ClaudeContextKind.Instructions && layer.Origin.Scope == ClaudeConfigScope.Project &&
            !layer.Path.Contains("/.claude/", StringComparison.Ordinal) && !layer.Path.Contains("\\.claude\\", StringComparison.Ordinal));
        if (!hasProjectInstructions)
            list.Children.Add(Offer("ClaudeOfferProjectMd", "Add CLAUDE.md", "Shared instructions for everyone on this project. Commit it and it stays with the repo.",
                () => Edit(new(new FileEdit(ClaudeFileTemplate.ProjectInstructions), "Create CLAUDE.md"))));
        if (!layers.Any(layer => layer.Path.EndsWith("CLAUDE.local.md", StringComparison.Ordinal)))
            list.Children.Add(Offer("ClaudeOfferLocalMd", "Add CLAUDE.local.md", "Local instructions, like which toolchain to use on this machine.",
                () => Edit(new(new FileEdit(ClaudeFileTemplate.ProjectLocalInstructions), "Create CLAUDE.local.md"))));
        // The appended prompt is not a memory file: /context folds it into the system prompt, and the host measures it
        // as the difference against a run without it. With several appended files the first row carries their sum.
        var appended = contextUsage?.Breakdown?.AppendedPromptTokens;
        var appendedRow = layers.FirstOrDefault(layer => layer.Kind == ClaudeContextKind.SystemPrompt);
        var breakdown = contextUsage?.Breakdown;
        var claudePrompt = breakdown?.Categories.FirstOrDefault(category => category.Name == "System prompt")?.Tokens is { } systemTokens
            ? systemTokens - (appendedRow is not null ? appended ?? 0 : 0) : (long?)null;
        var baseline = breakdown is null ? [] : BaselineRows(breakdown, claudePrompt);
        var total = new DockPanel { Name = "ClaudeContextTotal", Margin = new Thickness(8, 4) };
        var sum = memoryTokens.Values.Sum() + (appendedRow is not null ? appended ?? 0 : 0) + baseline.Sum(row => row.Tokens);
        var amount = measured is null ? Unmeasured() : Measure(sum, Ui.TextBrush, " in new session");
        SetDock(amount, Dock.Right);
        total.Children.Add(amount);
        total.Children.Add(new ClaudeContextLabel { Name = "ClaudeContextHeading" });
        list.Children.Add(new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = total });
        // The appended prompt is drawn as tereditor's instructions entry below, not as a file row.
        var files = layers.Where(layer => !ReferenceEquals(layer, appendedRow)).ToList();
        layers = files;
        // An @-import hangs under the file that pulled it in: a rule per ancestor level, then a tee or an elbow into the row.
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            var depth = Math.Min(layer.Depth ?? 0, 4);
            var nextDepth = index + 1 < layers.Count ? layers[index + 1].Depth ?? 0 : 0;
            var row = new DockPanel { Name = "ClaudeContextLayer", Tag = layer.Kind };
            foreach (var guide in ClaudeTree.Guides(depth, last: nextDepth < (layer.Depth ?? 0))) row.Children.Add(guide);
            var line = new DockPanel { Margin = new Thickness(8, 4) };
            var size = measured is null ? Unmeasured()
                : memoryTokens.TryGetValue(layer.Path, out var tokens) ? Measure(tokens, Ui.Muted)
                : layer.Lazy == true ? ClaudeParts.Code("—", Ui.Hint) : NotLoaded();
            line.Children.Add(SizeColumn(size));
            line.Children.Add(ClaudeTree.Glyph(Ui.Icon("fileText", Ui.Hint, 14), nextDepth > (layer.Depth ?? 0)));
            var column = new StackPanel();
            var title = Title(layer.Label);
            title.Children.Add(ClaudeParts.ScopeChip(layer.Origin.Scope.Name()));
            if (layer.Lazy == true)
            {
                var lazy = Ui.Text("ON DEMAND", Ui.Hint, 10);
                ToolTip.SetTip(lazy, "Loads only when Claude reads: " + string.Join(", ", layer.PathGlobs ?? []));
                title.Children.Add(lazy);
            }
            column.Children.Add(title);
            column.Children.Add(ClaudeParts.SourceButton(layer.Path, null, OpenSource));
            line.Children.Add(column);
            row.Children.Add(line);
            list.Children.Add(row);
        }
        // Files /context loaded that the resolver does not list (nested instructions, rules, imports) join with its label.
        var listed = layers.Select(layer => layer.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var file in measured ?? [])
        {
            if (listed.Contains(file.Name)) continue;
            var line = new DockPanel { Name = "ClaudeContextMeasuredFile", Tag = file.Name, Margin = new Thickness(8, 4) };
            var size = Measure(file.Tokens, Ui.Muted);
            line.Children.Add(SizeColumn(size));
            var icon = Ui.Icon("fileText", Ui.Hint, 14);
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            SetDock(icon, Dock.Left);
            line.Children.Add(icon);
            var column = new StackPanel();
            var title = Title(System.IO.Path.GetFileName(file.Name));
            title.Children.Add(ClaudeParts.ScopeChip(file.Source));
            column.Children.Add(title);
            column.Children.Add(ClaudeParts.SourceButton(file.Name, null, OpenSource));
            line.Children.Add(column);
            list.Children.Add(line);
        }
        // tereditor's instructions: one entry sized by the appended file, opening a popup of the sections the project can turn off.
        if (appendedRow is not null || context.Settings<ClaudeCodeSettings>().AppendSystemPrompt)
        {
            var instructions = InstructionSections(workspaceId);
            instructions.SetEntry(appendedRow is null ? null : measured is null ? Unmeasured() : appended is { } prompt ? Measure(prompt, Ui.Muted) : NotLoaded(),
                appendedRow is null ? null : ClaudeParts.SourceButton(appendedRow.Path, null, OpenSource));
            list.Children.Add(instructions);
        }
        // What else a fresh session starts with, as /context counts it: Claude's own prompt and tools, MCP and skills.
        rowDenied = snapshot?.Settings.FirstOrDefault(setting => setting.Key == "permissions.deny") is { Value.ValueKind: JsonValueKind.Array } deny
            ? deny.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet() : new HashSet<string>();
        rowSkillsOff = snapshot?.Capabilities.Where(item => item.Kind == ClaudeCapabilityKind.Skill && !item.Enabled).Select(item => item.Name).ToHashSet() ?? new HashSet<string>();
        rowSkillFiles = SkillFiles(snapshot?.Capabilities ?? []);
        foreach (var entry in baseline) AddBaselineRow(list, entry, depth: 0);
        return list;
    }

    private InstructionSectionsView InstructionSections(string workspace)
    {
        var view = new InstructionSectionsView("Claude Code",
            () => context.RequestAsync(ClaudeCodeContract.InstructionsList, new InstructionSectionQuery { WorkspaceId = workspace }).AsTask(),
            (id, enabled) => context.RequestAsync(ClaudeCodeContract.InstructionsSet, new InstructionSectionToggle { WorkspaceId = workspace, Id = id, Enabled = enabled }).AsTask(),
            message => context.Notify(PluginNotificationKind.Error, "Couldn't change tereditor's instructions", message));
        IDisposable? subscription = null;
        view.AttachedToVisualTree += (_, _) => subscription ??= context.Subscribe(ClaudeCodeContract.InstructionsChanged, _ => view.Refresh());
        view.DetachedFromVisualTree += (_, _) => { subscription?.Dispose(); subscription = null; };
        return view;
    }

    private Control SettingsSurface(ClaudeConfigSnapshot current)
    {
        var view = new DockPanel();
        var rows = new StackPanel();
        void Fill()
        {
            rows.Children.Clear();
            var shown = current.Settings.Where(entry => entry.Key.Contains(settingsQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var entry in shown) rows.Children.Add(SettingRow(entry, current.KnownSettingKeys));
            // After the set keys, the documented ones no file sets: listed while filtering, or on request.
            var unset = ClaudeSettingsReference.Unset(current.Settings.Select(entry => entry.Key), settingsQuery);
            if (shown.Length == 0 && unset.Count == 0) rows.Children.Add(Padded(Ui.Text("No keys match.", Ui.Muted, 13)));
            if (unset.Count == 0) return;
            if (settingsQuery.Length == 0 && !showDocumented)
            {
                var more = Ui.Button($"Show {unset.Count} documented settings that are not set", () => { showDocumented = true; Fill(); });
                more.Name = "ClaudeSettingShowDocumented";
                more.Margin = new Thickness(8);
                rows.Children.Add(more);
                return;
            }
            var heading = Ui.Text("NOT SET · DOCUMENTED BY CLAUDE CODE", Ui.Hint, 10);
            heading.Name = "ClaudeUnsetHeading";
            heading.Margin = new Thickness(8, 12, 8, 4);
            rows.Children.Add(heading);
            foreach (var entry in unset) rows.Children.Add(UnsetRow(entry, current.KnownSettingKeys));
        }
        var toolbar = ScopedSetting.SettingsToolbar("Claude", settingsQuery, query => { settingsQuery = query; Fill(); },
            () => Compose("", null, current.KnownSettingKeys));
        SetDock(toolbar, Dock.Top);
        view.Children.Add(toolbar);
        view.Children.Add(rows);
        Fill();
        return view;
    }

    private async void Compose(string key, JsonElement? value, IReadOnlyList<string> knownKeys)
    {
        if (ClaudeParts.Owner(this) is not { } owner) return;
        var composed = await SettingValueDialog.ShowAsync(owner,
            new("Claude", key, value, knownKeys, "Review the change")
            {
                KeyPlaceholder = "permissions.defaultMode",
                Suggestions = ClaudeSettingSuggestions.For,
                ShapeFor = ClaudeSettingSuggestions.ShapeFor
            });
        if (composed is not { } result) return;
        Edit(new(new SettingEdit(result.Key, result.Value), key.Length == 0 || value is null ? $"Add \"{result.Key}\"" : $"Change \"{result.Key}\""));
    }

    /// <summary>
    /// A documented key no file sets: its name (linked to its reference entry), type, one-line summary and default,
    /// and Set, which composes a value with the editor its type implies and reviews it like any other change. Keys
    /// only managed settings or <c>~/.claude.json</c> read say so instead of offering Set.
    /// </summary>
    private Control UnsetRow(ClaudeDocumentedSetting entry, IReadOnlyList<string> knownKeys)
    {
        var row = new StackPanel { Name = "ClaudeUnsetSetting", Tag = entry.Key, Spacing = 2, Margin = new Thickness(8, 4) };
        var head = new DockPanel();
        var scopes = ClaudeSettingsReference.WritableScopes(entry);
        Control action;
        if (scopes.Count > 0 && ClaudeSettingSuggestions.ShapeFor(entry.Key) is not null)
            action = ScopedSetting.RowAction("ClaudeSettingSet", "Set", () => Compose(entry.Key, null, knownKeys));
        else
        {
            var note = Ui.Text(entry.Scope == "Global config" ? "IN ~/.claude.json" : scopes.Count == 0 ? "MANAGED ONLY" : "EDIT AS A FILE", Ui.Hint, 10);
            note.Name = "ClaudeSettingUnsettable";
            ToolTip.SetTip(note, entry.Scope == "Global config" ? "Claude Code reads this key from ~/.claude.json, not from a settings file"
                : scopes.Count == 0 ? "Only managed settings, which your organization deploys, read this key"
                : "Only text, numbers, on/off and lists of text are editable here");
            note.VerticalAlignment = VerticalAlignment.Center;
            action = note;
        }
        SetDock(action, Dock.Right);
        head.Children.Add(action);
        var key = new WrapPanel { Orientation = Orientation.Horizontal };
        var docs = $"{ClaudeSettingsReference.DocsUrl}#{entry.Anchor}";
        var keyText = ClaudeParts.Code(entry.Key, Ui.TextBrush);
        var link = new Button
        {
            Name = "ClaudeSettingDocs",
            Tag = docs,
            Content = keyText,
            Padding = new Thickness(0),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        link.Classes.Add("quiet");
        link.Classes.Add("doc-link");
        keyText.ClearValue(TextBlock.ForegroundProperty);
        ToolTip.SetTip(link, "What this key does, in Claude Code's reference");
        link.Click += (_, _) => TopLevel.GetTopLevel(link)?.Launcher.LaunchUriAsync(new Uri(docs));
        key.Children.Add(link);
        var type = Ui.Text("  " + (entry.Values is { } values && entry.Type.EndsWith("one of", StringComparison.Ordinal) ? $"{entry.Type} {string.Join(", ", values)}" : entry.Type), Ui.Hint, 11);
        type.Name = "ClaudeUnsetType";
        type.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(type, entry.Type);
        key.Children.Add(type);
        head.Children.Add(key);
        row.Children.Add(head);
        var summary = ClaudeParts.Wrapped(entry.Summary, Ui.Muted);
        summary.Name = "ClaudeUnsetSummary";
        row.Children.Add(summary);
        if (entry.Default is { } fallback)
        {
            var preset = ClaudeParts.Wrapped("Default: " + fallback, Ui.Hint, 11);
            preset.Name = "ClaudeUnsetDefault";
            row.Children.Add(preset);
        }
        return new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }

    private Control SettingRow(ClaudeSettingValue entry, IReadOnlyList<string> knownKeys)
    {
        var text = Json(entry.Value);
        var editable = SettingValueDialog.ShapeOf(entry.Value) is not null;
        var valueText = ClaudeParts.Code(text, Ui.Accent);
        valueText.TextWrapping = TextWrapping.Wrap;
        valueText.TextTrimming = TextTrimming.CharacterEllipsis;
        valueText.MaxLines = 4;
        Control value;
        if (editable)
        {
            valueText.TextDecorations = TextDecorations.Underline;
            valueText.Name = "ClaudeSettingValueLink";
            var change = new Button
            {
                Name = "ClaudeSettingChange",
                Content = valueText,
                Padding = new Thickness(0),
                MinHeight = 0,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            change.Classes.Add("quiet");
            change.Classes.Add("value-link");
            ToolTip.SetTip(change, "Edit");
            change.Click += (_, _) => Compose(entry.Key, entry.Value, knownKeys);
            value = change;
        }
        else
        {
            ToolTip.SetTip(valueText, text);
            value = valueText;
        }
        var actions = new List<Control>();
        if (!editable)
        {
            var note = Ui.Text("EDIT AS A FILE", Ui.Hint, 10);
            note.Name = "ClaudeSettingUneditable";
            ToolTip.SetTip(note, "Only text, numbers, on/off and lists of text are editable here");
            actions.Add(note);
        }
        actions.Add(ScopedSetting.RowAction("ClaudeSettingRemove", "Remove", () => Edit(new(new SettingEdit(entry.Key), $"Remove \"{entry.Key}\"")), danger: true));
        var reference = ClaudeSettingsReference.NoteFor(entry.Key);
        return ScopedSetting.SettingRow("Claude", entry.Key, value, new(entry.Origin.Scope.Name(), entry.Origin.Path),
            [.. entry.Shadowed.Select(shadow => new ScopedSettingShadow(shadow.Origin.Scope.Name(), shadow.Origin.Path, Json(shadow.Value)))],
            path => OpenSource(path, entry.Origin.KeyPath), actions, entry.DocsUrl, "What this key does, in Claude Code's reference",
            reference?.Summary, reference?.Hint);
    }

    private static string Json(JsonElement value) => JsonSerializer.Serialize(value, ClaudeUIJson.Relaxed.JsonElement);

    // What the capabilities surface asks of the pane: open a source, review an edit, run one of Claude's commands.
    void IClaudeCapabilityHost.Open(string path, IReadOnlyList<string>? keyPath) => OpenSource(path, keyPath);
    void IClaudeCapabilityHost.Review(PendingEdit pending) => Edit(pending);
    bool IClaudeCapabilityHost.IsCollapsed(string key) => collapsedGroups.Contains(key);
    void IClaudeCapabilityHost.SetCollapsed(string key, bool collapsed) { if (collapsed) collapsedGroups.Add(key); else collapsedGroups.Remove(key); }

    async void IClaudeCapabilityHost.Uninstall(PluginUninstallTarget target)
    {
        if (ClaudeParts.Owner(this) is { } owner && await ReviewDialogs.UninstallAsync(context, owner, workspaceId, session?.TabKey, target)) Reload();
    }

    async void IClaudeCapabilityHost.Move(PluginMoveTarget target)
    {
        if (ClaudeParts.Owner(this) is { } owner && await ReviewDialogs.MoveAsync(context, owner, workspaceId, session?.TabKey, target)) Reload();
    }

    string? IClaudeCapabilityHost.ProjectRoot => snapshot?.Root;

    async void IClaudeCapabilityHost.MoveSkill(SkillMoveTarget target)
    {
        if (ClaudeParts.Owner(this) is { } owner && await ReviewDialogs.MoveSkillAsync(context, owner, workspaceId, session?.TabKey, target)) Reload();
    }

    async void IClaudeCapabilityHost.Marketplace(ClaudeMarketplaceAction action)
    {
        if (ClaudeParts.Owner(this) is { } owner && await ReviewDialogs.MarketplaceAsync(context, owner, workspaceId, session?.TabKey, action)) Reload();
    }

    async void IClaudeCapabilityHost.Add(string kind)
    {
        if (ClaudeParts.Owner(this) is not { } owner) return;
        var pending = kind switch
        {
            "mcp" => await ComposeDialogs.McpServerAsync(owner),
            "skill" => await ComposeDialogs.SkillAsync(owner),
            "hook" => await ComposeDialogs.HookAsync(owner),
            _ => await ComposeDialogs.PluginAsync(owner)
        };
        if (pending is not null) Edit(pending);
    }
}