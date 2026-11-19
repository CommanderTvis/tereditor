using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>
/// Makes a text a link-like click target: at rest it keeps its colour; while the pointer is over <c>trigger</c> (the text, or the whole row it
/// belongs to) or the trigger has keyboard focus, it turns accent and underlined. The trigger gets a pointer cursor, and a row (a
/// <see cref="Border"/>) also shows the hover fill.
/// </summary>
public static class HoverLink
{
    /// <summary>The decoration a hovered or keyboard-focused link text carries.</summary>
    public static readonly TextDecorationCollection Decoration = TextDecorations.Underline;

    /// <param name="text">The text that changes.</param>
    /// <param name="trigger">What the pointer hovers or focuses; the text itself when null.</param>
    /// <param name="activate">What Enter and Space do on a focused trigger; the trigger takes keyboard focus only when this is given.</param>
    public static T Apply<T>(T text, Control? trigger = null, Action? activate = null) where T : TextBlock
    {
        trigger ??= text;
        var rest = text.Foreground;
        var restFill = (trigger as Border)?.Background;
        var hovered = false;
        var focused = false;
        void Update()
        {
            var on = hovered || focused;
            text.Foreground = on ? Ui.Accent : rest;
            text.TextDecorations = on ? Decoration : null;
            if (trigger is Border row) row.Background = hovered ? Ui.Hover : restFill;
        }
        trigger.Cursor = new Cursor(StandardCursorType.Hand);
        trigger.PointerEntered += (_, _) => { hovered = true; Update(); };
        trigger.PointerExited += (_, _) => { hovered = false; Update(); };
        if (activate is not null)
        {
            trigger.Focusable = true;
            trigger.GotFocus += (_, e) => { focused = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional; Update(); };
            trigger.LostFocus += (_, _) => { focused = false; Update(); };
            trigger.KeyDown += (_, e) =>
            {
                if (e.Source != trigger || e.Key is not (Key.Enter or Key.Space)) return;
                e.Handled = true; activate();
            };
        }
        return text;
    }
}