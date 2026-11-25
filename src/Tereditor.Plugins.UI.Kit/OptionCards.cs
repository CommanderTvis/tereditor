using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>One choice of an <see cref="OptionCards"/> group: a stable id, a title and an optional one-line description.</summary>
public sealed class OptionCard(string id, string label, string? description = null)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string? Description { get; } = description;
}

/// <summary>
/// A vertical group of bordered cards where exactly one is selected, the look of the Appearance theme choices: title,
/// one-line description, and an accent background, border and check mark on the selected card. Each card is a radio
/// item (arrow keys move the choice, Space and Enter pick the focused card).
/// </summary>
public sealed class OptionCards : StackPanel
{
    private string? selected;

    public OptionCards()
    {
        Spacing = 4;
        AutomationProperties.SetControlTypeOverride(this, Avalonia.Automation.Peers.AutomationControlType.Group);
    }

    /// <summary>The cards, in order.</summary>
    public IReadOnlyList<OptionCard> Options { get; private set; } = [];

    /// <summary>The selected card's id, or null when none is.</summary>
    public string? SelectedId => selected;

    /// <summary>Raised when the user picks a card that is not selected yet; <see cref="Show"/> never raises it.</summary>
    public event Action<string>? Chosen;

    /// <summary>The name of the card for an id, so a check can find it.</summary>
    public string CardName(string id) => (Name ?? nameof(OptionCards)) + "_" + id;

    /// <summary>Replaces the cards and selects `selectedId`.</summary>
    public void SetOptions(IEnumerable<OptionCard> options, string? selectedId)
    {
        Options = [.. options];
        selected = selectedId;
        Children.Clear();
        foreach (var option in Options)
            Children.Add(Card(CardName(option.Id), option.Label, option.Description, option.Id == selected, () => Pick(option.Id), this));
    }

    /// <summary>Selects the card for an id, as a settings broadcast does; it raises nothing.</summary>
    public void Show(string? id)
    {
        selected = id;
        for (var i = 0; i < Options.Count && i < Children.Count; i++)
            if (Children[i] is Button card) Mark(card, Options[i].Id == id);
    }

    private void Pick(string id)
    {
        if (id == selected) return;
        Show(id);
        Chosen?.Invoke(id);
    }

    private void Step(string from, int direction)
    {
        var index = Options.ToList().FindIndex(option => option.Id == from) + direction;
        if (index < 0 || index >= Options.Count) return;
        Pick(Options[index].Id);
        Children[index].Focus();
    }

    /// <summary>
    /// One card button: the label, the description under it and a check mark at the right while `active`. The kit's
    /// Settings pages build their own cards with it, so every card looks the same.
    /// </summary>
    public static Button Card(string name, string label, string? description, bool active, Action select) => Card(name, label, description, active, select, null);

    private static Button Card(string name, string label, string? description, bool active, Action select, OptionCards? group)
    {
        var button = new CardButton(label, description) { Name = name };
        button.Click += (_, _) => select();
        if (group is not null)
            button.KeyDown += (_, e) =>
            {
                var direction = e.Key is Key.Down or Key.Right ? 1 : e.Key is Key.Up or Key.Left ? -1 : 0;
                if (direction == 0) return;
                group.Step(group.Options.First(option => group.CardName(option.Id) == name).Id, direction);
                e.Handled = true;
            };
        Mark(button, active);
        return button;
    }

    /// <summary>Draws a card as selected or not.</summary>
    public static void Mark(Button card, bool active)
    {
        if (card is CardButton shown) shown.Mark(active);
    }

    private sealed class CardButton : Button
    {
        private readonly TextBlock title;
        private readonly Control check;
        private readonly Grid row;
        private readonly bool described;

        public CardButton(string label, string? description)
        {
            described = description is not null;
            Padding = new(12, 8);
            MinHeight = 36;
            BorderThickness = new(1);
            CornerRadius = new(4);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            title = Ui.Text(label, Ui.TextBrush);
            row.Children.Add(title);
            if (description is not null)
            {
                var detail = Ui.Text(description, Ui.Muted, 12);
                Grid.SetRow(detail, 1);
                row.Children.Add(detail);
            }
            check = Ui.Icon("check", Ui.Accent);
            check.Name = "OptionCardCheck";
            Grid.SetRowSpan(check, 2);
            Grid.SetColumn(check, 1);
            Content = row;
            AutomationProperties.SetName(this, label);
            if (description is not null) AutomationProperties.SetHelpText(this, description);
            AutomationProperties.SetControlTypeOverride(this, Avalonia.Automation.Peers.AutomationControlType.RadioButton);
            Ui.FollowEnabled(this);
        }

        protected override Type StyleKeyOverride => typeof(Button);

        public void Mark(bool active)
        {
            title.Foreground = active || described ? Ui.TextBrush : Ui.Muted;
            if (active && !row.Children.Contains(check)) row.Children.Add(check);
            else if (!active) row.Children.Remove(check);
            Background = active ? Ui.PrimarySubtle : Ui.Elevated;
            BorderBrush = active ? Ui.PrimaryMuted : Ui.BorderBrush;
        }
    }
}
