using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.CodeIntelligence.UI;

/// <summary>
/// A settings row for a number that can be switched off: a check box with the label, then the number and its unit. Unchecked, the
/// number is greyed out and the setting is off (<see cref="Value"/> is null); the number keeps what it showed, so checking the box
/// again starts from it. Callers map null to the host's own off value; no number in the UI means "off".
/// </summary>
internal sealed class OptionalNumberRow : Border
{
    /// <summary>The shared size group of the unit column: put the rows under a <c>Grid.IsSharedSizeScope</c> parent.</summary>
    internal const string NumberUnitGroup = "NumberUnit";

    private readonly CheckBox check = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericUpDown input = new()
    {
        Minimum = 1,
        Maximum = int.MaxValue,
        Increment = 1,
        FormatString = "0",
        IsEnabled = false,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock unit = new() { Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };

    public OptionalNumberRow()
    {
        // The page that holds the rows is a shared size scope: every row has the same fixed box column and one shared unit column, so boxes and units line up.
        Grid.SetColumn(input, 1);
        Grid.SetColumn(unit, 2);
        Child = new Grid { ColumnDefinitions = new("*,128,Auto") { [2] = { SharedSizeGroup = NumberUnitGroup } }, ColumnSpacing = 8, Children = { check, input, unit } };
        check.IsCheckedChanged += (_, _) => input.IsEnabled = check.IsChecked == true;
    }

    /// <summary>The text beside the check box, such as "Stop idle servers after".</summary>
    public string Label { get => check.Content as string ?? ""; set { check.Content = value; AutomationProperties.SetName(check, value); AutomationProperties.SetName(input, value + " " + unit.Text); } }

    /// <summary>The unit after the number, such as "minutes".</summary>
    public string Unit { get => unit.Text ?? ""; set { unit.Text = value; AutomationProperties.SetName(input, check.Content + " " + value); } }

    public int Minimum { get => (int)input.Minimum; set => input.Minimum = value; }

    public int Maximum { get => (int)input.Maximum; set => input.Maximum = value; }

    /// <summary>The check box, for tests and focus.</summary>
    public CheckBox Check => check;

    /// <summary>The number box, for tests and focus.</summary>
    public NumericUpDown Input => input;

    /// <summary>Whether the setting is on.</summary>
    public bool IsOn => check.IsChecked == true;

    /// <summary>The number shown, on or off; null when the box is empty.</summary>
    public int? Number => input.Value is { } value ? (int)value : null;

    /// <summary>The number when on, null when off.</summary>
    public int? Value => IsOn ? Number : null;

    /// <summary>Shows a saved value: a number turns the row on with it, null turns it off and shows <paramref name="whenOff"/>.</summary>
    public void Show(int? value, int whenOff)
    {
        check.IsChecked = value is not null;
        input.Value = value ?? whenOff;
    }

    /// <summary>Changes the number shown while the row is off, such as the automatic value; an on row keeps what the user set.</summary>
    public void ShowWhenOff(int number)
    {
        if (!IsOn) input.Value = number;
    }
}