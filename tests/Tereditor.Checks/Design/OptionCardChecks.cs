using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks.Design;

/// <summary>The kit's OptionCards group: cards with descriptions, one selected with a check, picks by pointer and keys.</summary>
internal static class OptionCardChecks
{
    internal static void Run()
    {
        var cards = new OptionCards { Name = "Presentation", Width = 400 };
        cards.SetOptions(
        [
            new("terminal", "Terminal", "Run the agent in a terminal tab."),
            new("chat", "Chat", "Use the chat view with history, attachments and review."),
            new("other", "Other", "A third choice.")
        ], "terminal");
        var window = new Window { Width = 420, Height = 300, Content = cards };
        window.Show();
        Settle();
        var picked = new List<string>();
        cards.Chosen += picked.Add;

        List<Button> Buttons() => [.. cards.GetLogicalChildren().OfType<Button>()];
        bool Checked(Button card) => card.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "OptionCardCheck");
        Require(Buttons().Count == 3, "The group builds one card per option.");
        Require(Buttons().Select(card => card.Name).SequenceEqual(["Presentation_terminal", "Presentation_chat", "Presentation_other"]), "Cards are named after the group and the option id.");
        Require(Buttons().All(card => (AutomationProperties.GetHelpText(card)?.Length ?? 0) > 0), "Every card carries its description for assistive technology.");
        Require(Buttons().All(card => card.GetLogicalDescendants().OfType<TextBlock>().Count() == 2), "Every card shows a title and a description.");
        Require(Buttons().All(card => PeerType(card) == AutomationControlType.RadioButton), "Each card is a radio item.");
        Require(Checked(Buttons()[0]) && !Checked(Buttons()[1]) && cards.SelectedId == "terminal", "Only the selected card shows the check.");

        cards.Show("chat");
        Require(cards.SelectedId == "chat" && Checked(Buttons()[1]) && !Checked(Buttons()[0]) && picked.Count == 0, "Show moves the check without raising a pick.");

        Buttons()[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(cards.SelectedId == "other" && Checked(Buttons()[2]) && picked.SequenceEqual(["other"]), "Clicking a card selects it and raises one pick.");
        Buttons()[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(picked.Count == 1, "Clicking the selected card raises nothing.");

        Buttons()[2].Focus();
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Settle();
        Require(cards.SelectedId == "chat" && picked.SequenceEqual(["other", "chat"]) && Buttons()[1].IsFocused, "Arrow Up picks and focuses the previous card.");
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Settle();
        Require(cards.SelectedId == "other" && picked[^1] == "other", "Arrow Down picks the next card and stops at the last.");

        cards.SetOptions([new("a", "A"), new("b", "B")], "b");
        Require(Buttons().Count == 2 && Buttons().All(card => card.GetLogicalDescendants().OfType<TextBlock>().Count() == 1) && cards.SelectedId == "b", "SetOptions rebuilds the cards; a card without a description shows only its title.");
        window.Close();
        Console.WriteLine("PASS option cards: cards with descriptions, one check, pick by click and arrow keys, radio items");
    }

    private static AutomationControlType PeerType(Control control) =>
        Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(control).GetAutomationControlType();

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
