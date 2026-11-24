using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;
using Tereditor.UI.Plugins;

namespace Tereditor.UI.Panels;

/// <summary>
/// What the Agent pane remembers per workspace: which agent's chip is shown, the sub-agent, the tab, and whether the user chose the chip
/// by hand. A focus change to a tab an agent owns takes the chip back (clearing the hand choice); any other focus keeps everything.
/// </summary>
internal sealed class AgentPaneState
{
    public string? Chip { get; private set; }
    public string? SubAgent { get; private set; }
    public AgentPaneTab Tab { get; set; } = AgentPaneTab.Context;
    public bool Manual { get; private set; }

    /// <summary>The focused tab last seen, so a repeated report of the same tab changes nothing.</summary>
    public string? LastKey { get; set; }

    public void Follow(string chip, string? subAgent) { Chip = chip; SubAgent = subAgent; Manual = false; }
    public void Choose(string chip) { Chip = chip; Manual = true; }
    public void ChooseSubAgent(string? subAgent) { SubAgent = subAgent; Manual = true; }

    /// <summary>The chip to show: the chosen one while it is still offered, else the first.</summary>
    public string? Shown(IReadOnlyList<string> chips) => Chip is not null && chips.Contains(Chip) ? Chip : chips.FirstOrDefault();

    /// <summary>
    /// Reads the focus: <paramref name="key"/> names the focused tab, <paramref name="owner"/> the chip that holds it (or null).
    /// Returns whether the chip or sub-agent moved.
    /// </summary>
    public bool Observe(string? key, string? owner, string? subAgent)
    {
        if (key is null) return false;
        var changed = key != LastKey;
        LastKey = key;
        if (owner is null || (!changed && Manual)) return false;
        var moved = Chip != owner || SubAgent != subAgent;
        Follow(owner, subAgent);
        return moved;
    }
}

/// <summary>
/// The Agent side pane: one chip per usable agent, an optional sub-agent picker, and the four tabs Context, Settings, Capabilities and Account.
/// Agents supply the content; this control only composes it and follows the focused document.
/// </summary>
internal sealed class AgentPane : DockPanel
{
    private readonly PluginRegistry registry;
    private readonly Func<string> workspace;
    private readonly Func<(string Key, TabRef Tab)?> focused;
    private readonly Func<string, Func<Control>, Control> mount;
    private readonly Dictionary<string, AgentPaneState> states = [];
    private readonly Dictionary<string, (Control Content, Control Host, string Workspace)> contents = [];
    private readonly WrapPanel chips = new() { Name = "AgentChips", Margin = new Thickness(8, 4, 8, 0) };
    private readonly StackPanel tabs = new() { Name = "AgentTabs", Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 4) };
    private readonly ComboBox picker = new() { Name = "AgentSubAgents", Margin = new Thickness(8, 4, 8, 0), HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
    private readonly ContentControl body = new() { Name = "AgentBody", HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ScrollViewer strip;
    private string chipsKey = "";
    private string? shownChip;
    private string? appliedSub;
    private string pickerKey = "";
    private bool pickerUpdating;

    public AgentPane(PluginRegistry registry, Func<string> workspace, Func<(string Key, TabRef Tab)?> focused, Func<string, Func<Control>, Control> mount)
    {
        this.registry = registry; this.workspace = workspace; this.focused = focused; this.mount = mount;
        Name = "AgentPane";
        Background = Ui.Sidebar;
        SetDock(chips, Dock.Top); Children.Add(chips);
        SetDock(picker, Dock.Top); Children.Add(picker);
        strip = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SetDock(strip, Dock.Top); Children.Add(strip);
        foreach (var tab in Enum.GetValues<AgentPaneTab>())
        {
            var segment = Ui.Segment("AgentTab_" + tab.ToString().ToLowerInvariant(), tab.ToString());
            segment.Tag = tab;
            segment.Click += (_, _) => { State.Tab = tab; Render(); };
            tabs.Children.Add(segment);
        }
        picker.SelectionChanged += (_, _) =>
        {
            if (pickerUpdating || picker.SelectedItem is not AgentSubAgent chosen) return;
            State.ChooseSubAgent(chosen.Id);
            Render();
        };
        Children.Add(body);
        Sync();
    }

    private AgentPaneState State
    {
        get
        {
            var key = workspace();
            if (!states.TryGetValue(key, out var state)) states[key] = state = new();
            return state;
        }
    }

    /// <summary>The chips offered now, in the fixed order of the agents: Claude Code, Codex, Pi, omp, ACP, then any other.</summary>
    internal IReadOnlyList<(string Key, PluginRow<AgentPaneRegistration> Row)> Offered()
    {
        string[] order = ["claude-code", "codex", "pi", "omp", "acp"];
        return [.. registry.AgentPanes
            .Where(row => registry.Active.Contains(row.PluginId) && Safely(() => row.Value.Usable?.Invoke() ?? true))
            .OrderBy(row => Array.IndexOf(order, row.PluginId) is var at and >= 0 ? at : order.Length)
            .Select(row => (row.PluginId + ":" + row.Value.Id, row))];
    }

    private static bool Safely(Func<bool> read)
    {
        try { return read(); }
        catch (Exception error) { Console.Error.WriteLine("Agent pane callback failed: " + error.Message); return false; }
    }

    /// <summary>Reads the focus, the roster and the registrations again and redraws what changed. Cheap; called on every layout or plugin change.</summary>
    internal void Sync()
    {
        var offered = Offered();
        var state = State;
        if (focused() is { } focus)
        {
            string? owner = null, sub = null;
            foreach (var (key, row) in offered)
                if (row.Value.Owns is { } owns && Safely(() => owns(focus.Tab)))
                {
                    owner = key;
                    sub = row.Value.SubAgentOf is { } of ? Try(() => of(focus.Tab)) : null;
                    break;
                }
            state.Observe(focus.Key, owner, sub);
        }
        Render(offered);
    }

    private static string? Try(Func<string?> read)
    {
        try { return read(); }
        catch (Exception error) { Console.Error.WriteLine("Agent pane callback failed: " + error.Message); return null; }
    }

    private void Render() => Render(Offered());

    private void Render(IReadOnlyList<(string Key, PluginRow<AgentPaneRegistration> Row)> offered)
    {
        var state = State;
        var keys = offered.Select(item => item.Key).ToArray();
        shownChip = state.Shown(keys);
        var signature = string.Join('|', offered.Select(item => item.Key + "=" + item.Row.Value.Label)) + "#" + shownChip;
        if (signature != chipsKey)
        {
            chipsKey = signature;
            chips.Children.Clear();
            foreach (var (key, row) in offered)
            {
                var chip = Ui.Segment("AgentChip_" + row.Value.Id, row.Value.Label);
                chip.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { PluginIcons.Resolve(row.Value.Icon, registry.Entry(row.PluginId), Ui.Muted, 14), new TextBlock { Text = row.Value.Label, VerticalAlignment = VerticalAlignment.Center } }
                };
                chip.Height = 24;
                chip.Tag = key;
                chip.IsChecked = key == shownChip;
                chip.Click += (_, _) => { State.Choose(key); Render(); };
                chips.Children.Add(chip);
            }
        }
        chips.IsVisible = offered.Count > 0;
        strip.IsVisible = offered.Count > 0;
        foreach (var segment in tabs.Children.OfType<ToggleButton>()) segment.IsChecked = Equals(segment.Tag, state.Tab);
        if (shownChip is null)
        {
            picker.IsVisible = false;
            var none = Ui.Text("No agent is enabled. Turn one on in Settings, Plugins.", Ui.Muted, 13);
            none.Name = "AgentNone"; none.Margin = new Thickness(8); none.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            body.Content = none;
            return;
        }
        var shown = offered.First(item => item.Key == shownChip).Row;
        var current = workspace();
        if (contents.TryGetValue(shownChip, out var held) && held.Workspace != current)
        {
            var kept = shown.Value.Retarget is { } retarget && Safe(() => retarget(held.Content, current));
            if (kept) contents[shownChip] = held with { Workspace = current };
            else contents.Remove(shownChip);
        }
        if (!contents.TryGetValue(shownChip, out held))
        {
            var host = mount(shown.PluginId, () => shown.Value.Create(current));
            var content = host is ContentControl { Content: Control inner } ? inner : host;
            contents[shownChip] = held = (content, host, current);
            appliedSub = null; pickerKey = "";
        }
        ShowPicker(shown, state, current, held.Content);
        if (shown.Value.ShowTab is { } show) { try { show(held.Content, state.Tab); } catch (Exception error) { Console.Error.WriteLine("Agent pane tab failed: " + error.Message); } }
        if (!ReferenceEquals(body.Content, held.Host)) body.Content = held.Host;
    }

    private static bool Safe(Func<bool> read)
    {
        try { return read(); }
        catch (Exception error) { Console.Error.WriteLine("Agent pane retarget failed: " + error.Message); return false; }
    }

    private void ShowPicker(PluginRow<AgentPaneRegistration> row, AgentPaneState state, string current, Control content)
    {
        IReadOnlyList<AgentSubAgent> list = [];
        if (row.Value.SubAgents is { } subs) { try { list = subs(current); } catch (Exception error) { Console.Error.WriteLine("Agent pane sub-agents failed: " + error.Message); } }
        picker.IsVisible = list.Count > 0;
        if (list.Count == 0) return;
        var key = row.PluginId + "|" + string.Join('|', list.Select(item => item.Id + "=" + item.Label));
        pickerUpdating = true;
        try
        {
            if (key != pickerKey) { pickerKey = key; picker.ItemsSource = list; picker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AgentSubAgent>((item, _) => new TextBlock { Text = item?.Label }); }
            var wanted = list.FirstOrDefault(item => item.Id == state.SubAgent) ?? list[0];
            if (!Equals(picker.SelectedItem, wanted)) picker.SelectedItem = wanted;
            if (appliedSub != wanted.Id && row.Value.SelectSubAgent is { } select)
            {
                appliedSub = wanted.Id;
                try { select(content, wanted.Id); } catch (Exception error) { Console.Error.WriteLine("Agent pane sub-agent failed: " + error.Message); }
            }
        }
        finally { pickerUpdating = false; }
    }
}