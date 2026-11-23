using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>
/// One provider in a key list: a title row with Remove key (when a key is stored) and Add key or Replace key on the right, hints under it, and a
/// masked key form that opens under the row. The key is sent once through <c>save</c> and never shown again. Settings › Plugins › Model providers
/// and the plugins that fill it use this one row.
/// </summary>
public sealed class ApiKeyRow : StackPanel
{
    private readonly string providerName;
    private readonly Action<string> save;
    private readonly Action remove;
    private string title = "";
    private bool stored;
    private bool open;

    public ApiKeyRow(string providerName, Action<string> save, Action remove)
    {
        this.providerName = providerName;
        this.save = save;
        this.remove = remove;
        Spacing = 4;
    }

    /// <summary>Buttons placed after the key buttons, for example Remove provider, as a label and what it does.</summary>
    public IList<(string Label, Action Action)> ExtraActions { get; } = [];

    /// <summary>Hints shown under the title row, in muted text.</summary>
    public IList<string> Hints { get; } = [];

    /// <summary>Raised when the user opens or closes the key form, so a page can keep one form open at a time.</summary>
    public Action<bool>? OpenChanged { get; set; }

    /// <summary>Whether the key form is shown.</summary>
    public bool Open
    {
        get => open;
        set { open = value; Rebuild(); }
    }

    /// <summary>Shows the row for a title and whether a key is stored, with the current hints and extra actions.</summary>
    public void Show(string title, bool stored)
    {
        this.title = title;
        this.stored = stored;
        Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        var line = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        DockPanel.SetDock(actions, Dock.Right);
        if (stored) actions.Children.Add(Ui.Button("Remove key", remove));
        actions.Children.Add(Ui.Button(stored ? "Replace key" : "Add key", () => { open = !open; Rebuild(); OpenChanged?.Invoke(open); }));
        foreach (var (label, action) in ExtraActions) actions.Children.Add(Ui.Button(label, action));
        line.Children.Add(actions);
        line.Children.Add(Ui.Text(title, Ui.TextBrush, 13));
        Children.Add(line);
        foreach (var hint in Hints)
        {
            var text = Ui.Text(hint, Ui.Muted, 12);
            text.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            Children.Add(text);
        }
        if (open) Children.Add(KeyForm());
    }

    private KeyFormPanel KeyForm()
    {
        var key = new TextBox { PasswordChar = '•', PlaceholderText = "API key", Name = "ModelProviderKey", };
        AutomationProperties.SetName(key, "API key for " + providerName);
        var row = new KeyFormPanel();
        void Save()
        {
            var secret = key.Text ?? "";
            key.Text = "";
            if (secret.Trim().Length == 0) return;
            open = false;
            Rebuild();
            OpenChanged?.Invoke(false);
            save(secret);
        }
        key.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Save(); e.Handled = true; } };
        var saveButton = Ui.Button("Save", Save);
        var cancel = Ui.Button("Cancel", () => { open = false; Rebuild(); OpenChanged?.Invoke(false); });
        row.Children.Add(key);
        row.Children.Add(saveButton);
        row.Children.Add(cancel);
        return row;
    }

    /// <summary>The key field with Save and Cancel: all on one line when the field keeps its minimum width, else the buttons wrap under the field.</summary>
    internal sealed class KeyFormPanel : Panel
    {
        internal const double FieldMinWidth = 200;
        private const double Gap = 8;

        private bool Wrapped(double width, Size buttons) => width < FieldMinWidth + Gap + buttons.Width;

        private Size ButtonsSize()
        {
            var inf = Size.Infinity;
            Children[1].Measure(inf);
            Children[2].Measure(inf);
            var buttons = new Size(Children[1].DesiredSize.Width + Gap + Children[2].DesiredSize.Width, Math.Max(Children[1].DesiredSize.Height, Children[2].DesiredSize.Height));
            return buttons;
        }

        protected override Size MeasureOverride(Size available)
        {
            if (Children.Count < 3) return default;
            var buttons = ButtonsSize();
            var width = double.IsInfinity(available.Width) ? FieldMinWidth + Gap + buttons.Width : available.Width;
            var wrapped = Wrapped(width, buttons);
            Children[0].Measure(new Size(wrapped ? width : width - Gap - buttons.Width, double.PositiveInfinity));
            var field = Children[0].DesiredSize;
            return wrapped ? new Size(width, field.Height + Gap + buttons.Height) : new Size(width, Math.Max(field.Height, buttons.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Children.Count < 3) return finalSize;
            var buttons = ButtonsSize();
            var save = Children[1].DesiredSize;
            var cancel = Children[2].DesiredSize;
            var field = Children[0].DesiredSize;
            if (Wrapped(finalSize.Width, buttons))
            {
                Children[0].Arrange(new Rect(0, 0, finalSize.Width, field.Height));
                var y = field.Height + Gap;
                Children[1].Arrange(new Rect(0, y, save.Width, save.Height));
                Children[2].Arrange(new Rect(save.Width + Gap, y, cancel.Width, cancel.Height));
            }
            else
            {
                var fieldWidth = finalSize.Width - Gap - buttons.Width;
                Children[0].Arrange(new Rect(0, 0, fieldWidth, finalSize.Height));
                Children[1].Arrange(new Rect(fieldWidth + Gap, 0, save.Width, finalSize.Height));
                Children[2].Arrange(new Rect(fieldWidth + Gap + save.Width + Gap, 0, cancel.Width, finalSize.Height));
            }
            return finalSize;
        }
    }
}