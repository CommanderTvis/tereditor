using Avalonia.Controls;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.UI.Plugins;

namespace Tereditor.UI.Plugins;

/// <summary>
/// A named place in one of core's pages (W23): the sections active plugins contribute to it, each under a rule. It
/// follows the registry while shown, so enabling or disabling a plugin adds or removes its section without a
/// placeholder, and a section is rebuilt only when the set of contributing registrations changes.
/// </summary>
internal sealed class PluginSlot : StackPanel
{
    private readonly PluginRegistry registry;
    private readonly string slot;
    private string? workspace;
    private readonly bool bare;
    private readonly string? emptyHint;
    private bool synced;
    private IReadOnlyList<PluginRow<SlotSectionRegistration>> shown = [];

    /// <param name="bare">Sections have no frame or rule; used where a plugin embeds the slot in its own page.</param>
    /// <param name="emptyHint">A plain sentence shown while no plugin contributes a section.</param>
    internal PluginSlot(PluginRegistry registry, string slot, string? workspace, bool bare = false, string? emptyHint = null)
    {
        this.registry = registry; this.slot = slot; this.workspace = workspace; this.bare = bare; this.emptyHint = emptyHint;
        Name = "PluginSlot";
        AttachedToVisualTree += (_, _) => { registry.Changed += Changed; Sync(); };
        DetachedFromVisualTree += (_, _) => { registry.Changed -= Changed; Children.Clear(); shown = []; synced = false; };
    }

    /// <summary>Moves the slot to another workspace: the sections of the old one are dropped and built again for the new one.</summary>
    internal void Retarget(string? next)
    {
        if (next == workspace) return;
        workspace = next;
        shown = [];
        synced = false;
        Children.Clear();
        if (TopLevel.GetTopLevel(this) is not null) Sync();
    }

    private void Changed(PluginTables tables)
    {
        if ((tables & (PluginTables.SlotSections | PluginTables.Active | PluginTables.Roster)) == 0) return;
        if (Dispatcher.UIThread.CheckAccess()) Sync(); else Dispatcher.UIThread.Post(Sync);
    }

    private void Sync()
    {
        var next = registry.SlotSectionsFor(slot);
        if (synced && next.SequenceEqual(shown)) return;
        synced = true;
        shown = next;
        Children.Clear();
        if (next.Count == 0 && emptyHint is not null)
            Children.Add(new TextBlock { Name = "PluginSlotHint", Text = emptyHint, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Tereditor.Plugins.UI.Kit.Ui.Muted, FontSize = 12 });
        foreach (var row in next)
            if (bare) Children.Add(WorkbenchWindow.Mount(row.PluginId, () => row.Value.Create(workspace)));
            else Children.Add(new Border
            {
                Name = "PluginSlotSection_" + row.PluginId,
                BorderBrush = Tereditor.Plugins.UI.Kit.Ui.BorderBrush,
                BorderThickness = new(0, 1, 0, 0),
                Margin = new(0, 16, 0, 0),
                Padding = new(0, 16, 0, 0),
                Child = WorkbenchWindow.Mount(row.PluginId, () => row.Value.Create(workspace))
            });
    }
}