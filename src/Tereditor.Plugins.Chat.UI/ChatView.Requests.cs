using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Chat.UI;

// Permission and question requests, after the extension: the first request still waiting is a panel above the composer (first in, first out),
// the transcript behind it dims, and the composer says it is waiting. What stays in the transcript is the tool's own card.
internal sealed partial class ChatView
{
    private readonly Border requestPanel = new()
    {
        Name = "ChatRequestPanel",
        IsVisible = false,
        Padding = new Thickness(10),
        Margin = new Thickness(12, 0, 12, 4),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1)
    };
    private string requestSignature = "";
    private bool requestFolded;
    private int destinationIndex = -1;
    private int questionTab;
    private DispatcherTimer? advance;
    private TextBox? reasonBox, commandBox;

    internal Border RequestPanel => requestPanel;
    internal string? RequestPrompt => requestPanel.IsVisible ? requestPanel.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "ChatRequestTitle")?.Text : null;

    private string RequestSignature(ChatItem first) => first.Id + "|" + requestFolded + "|" + destinationIndex + "|" + questionTab + "|" +
        (first is ChatQuestionItem question ? string.Join(";", transcript.DraftOf(question).Chosen.Select(chosen => string.Join(",", chosen))) + string.Join(";", transcript.DraftOf(question).Other) : "");

    private void UpdateRequests(bool force = false)
    {
        var first = transcript.FirstPending();
        if (first is null)
        {
            requestPanel.IsVisible = false;
            requestSignature = "";
            list.Opacity = 1;
            input.PlaceholderText = Placeholder();
            return;
        }
        var signature = RequestSignature(first);
        if (signature == requestSignature && !force) return;
        var changed = requestPanel.Child is null || !requestSignature.StartsWith(first.Id + "|", StringComparison.Ordinal);
        requestSignature = signature;
        requestPanel.Background = Ui.Elevated;
        requestPanel.BorderBrush = Ui.Accent;
        requestPanel.IsVisible = true;
        if (changed) { requestFolded = false; questionTab = 0; destinationIndex = -1; }
        requestPanel.Child = first switch
        {
            ChatPermissionItem permission => BuildPermission(permission),
            ChatQuestionItem asked => BuildQuestion(asked),
            _ => null
        };
        // The transcript dims behind a permission request, not behind a question.
        list.Opacity = first is ChatQuestionItem ? 1 : 0.55;
        input.PlaceholderText = Placeholder();
        if (changed) Dispatcher.UIThread.Post(() => (requestPanel.Child as Control)?.Focus(), DispatcherPriority.Background);
    }

    private static string Destination(ChatPermissionItem item, int index) =>
        index >= 0 && index < item.Destinations.Count ? item.Destinations[index].Label : "";

    /// <summary>A label with no colour of its own, so it inherits the foreground of the button that holds it.</summary>
    internal static TextBlock Inherit(string text, double size) => new() { Text = text, FontSize = size, FontWeight = Ui.InterfaceWeight, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private Button Choice(string name, string key, string label, Action run, bool primary = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        // A primary button's labels wear no colour of their own: they take the button's, so a disabled one reads as disabled.
        row.Children.Add(primary ? Inherit(key, 11) : Ui.Text(key, Ui.Hint, 11));
        row.Children.Add(primary ? Inherit(label, 12) : Ui.Text(label, Ui.TextBrush, 12));
        var button = new Button
        {
            Name = name,
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4),
            MinHeight = 0,
            Background = primary ? null : Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        if (primary) button.Classes.Add("primary");
        Avalonia.Automation.AutomationProperties.SetName(button, key + " " + label);
        button.Click += (_, _) => run();
        return button;
    }

    private readonly List<ChatPlanComment> planQuotes = [];

    private Control BuildPermission(ChatPermissionItem item)
    {
        var plan = item.ToolName == "ExitPlanMode";
        var root = new StackPanel { Name = "ChatPermission", Spacing = 6, Focusable = true };
        var head = new DockPanel();
        var fold = Ui.IconButton(requestFolded ? "arrowRight" : "arrowDown", requestFolded ? "Show the request" : "Fold the request", () => { requestFolded = !requestFolded; UpdateRequests(true); });
        fold.Name = "ChatRequestFold";
        DockPanel.SetDock(fold, Dock.Right);
        head.Children.Add(fold);
        var title = new TextBlock { Name = "ChatRequestTitle", Text = item.Prompt ?? (plan ? "Accept this plan?" : $"Do you want to proceed with {item.ToolName}?"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Ui.TextBrush, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(title);
        root.Children.Add(head);
        if (requestFolded) return root;

        commandBox = null;
        var offered = item.Options.Count > 0;
        string Str(string name) => item.Input.ValueKind == JsonValueKind.Object && item.Input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        // An agent that sent its own answers (ACP) is shown what it said the call is: kind, files, the command or input, diffs.
        if (offered) AcpDetails(root, item);
        else switch (item.ToolName)
            {
                case "Bash":
                    // The command is editable; what is accepted replaces the call's own.
                    commandBox = new TextBox { Name = "ChatPermCommand", Text = Str("command"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.CodeFont, FontSize = 12, MaxHeight = 160 };
                    root.Children.Add(commandBox);
                    if (Str("description") is { Length: > 0 } description) root.Children.Add(Ui.Text(description, Ui.Muted, 11));
                    break;
                case "Edit" or "MultiEdit" or "Write" or "NotebookEdit":
                    var open = Ui.Button("Open diff", () => _ = OpenDiff(item));
                    open.Name = "ChatPermDiff";
                    root.Children.Add(Ui.Text(Str("file_path"), Ui.Muted, 12));
                    root.Children.Add(open);
                    break;
                case "ExitPlanMode":
                    var text = new SelectableTextBlock { Name = "ChatPlanBody", Text = Str("plan"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.TextBrush };
                    root.Children.Add(new ScrollViewer { MaxHeight = 220, Content = text, Name = "ChatPlanText" });
                    // Selecting text of the plan and commenting on it sends the quote with the comment.
                    var quoted = Ui.Text("", Ui.Muted, 11);
                    quoted.Name = "ChatPlanQuotes";
                    quoted.IsVisible = false;
                    var comment = Ui.Button("Comment on selection", () =>
                    {
                        var quote = text.SelectedText?.Trim();
                        if (string.IsNullOrEmpty(quote) || reasonBox is null) return;
                        planQuotes.Add(new ChatPlanComment(quote, reasonBox.Text?.Trim() ?? ""));
                        reasonBox.Text = "";
                        quoted.Text = $"{planQuotes.Count} comment(s) on the plan will be sent";
                        quoted.IsVisible = true;
                    });
                    comment.Name = "ChatPlanComment";
                    root.Children.Add(comment);
                    root.Children.Add(quoted);
                    break;
                default:
                    if (item.Description is { Length: > 0 } detail) root.Children.Add(Ui.Text(detail, Ui.Muted, 12));
                    var json = JsonSerializer.Serialize(item.Input, ChatUIJson.Indented.JsonElement);
                    var expander = new Expander { Header = "Details", Content = new TextBlock { Text = json, FontFamily = Ui.CodeFont, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted }, IsExpanded = false };
                    root.Children.Add(expander);
                    break;
            }

        // ACP has no way to send a reason with a rejection, so its card has no such box.
        reasonBox = offered ? null : new TextBox { Name = "ChatPermReason", PlaceholderText = plan ? $"Add feedback for {env.Agent.Short}" : $"Tell {env.Agent.Short} what to do instead", FontSize = 12 };
        if (reasonBox is not null) reasonBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Reject(item); e.Handled = true; } };
        var choices = new List<Button>();
        if (offered)
        {
            // The agent's own answers, in its order, with its words; the first "allow once" (else the first) is the primary one.
            var main = Math.Max(0, item.Options.ToList().FindIndex(option => option.Kind == "allow_once"));
            for (var at = 0; at < item.Options.Count; at++)
            {
                var option = item.Options[at];
                choices.Add(Choice("ChatPermOption_" + at, (at + 1).ToString(), option.Label, () => _ = AnswerPermission(item, option.Kind.StartsWith("allow", StringComparison.Ordinal), optionId: option.Id), at == main));
            }
        }
        else if (plan)
        {
            choices.Add(Choice("ChatPermYes", "1", "Yes, and auto-accept", () => _ = AnswerPermission(item, true, planMode: "acceptEdits"), true));
            choices.Add(Choice("ChatPermManual", "2", "Yes, and manually approve edits", () => _ = AnswerPermission(item, true, planMode: "default")));
            choices.Add(Choice("ChatPermNo", "3", string.IsNullOrWhiteSpace(reasonBox!.Text) ? "No, keep planning" : "Send feedback and keep planning", () => Reject(item)));
        }
        else
        {
            choices.Add(Choice("ChatPermYes", "1", "Yes", () => _ = AnswerPermission(item, true, updated: EditedInput(item)), true));
            if (item.AlwaysLabel is { Length: > 0 } alwaysLabel)
            {
                if (destinationIndex < 0) destinationIndex = Math.Max(0, item.Destinations.ToList().FindIndex(d => d.Key == (Composer.Destination ?? item.DefaultDestination)));
                var scoped = item.AlwaysScoped && item.Destinations.Count > 0;
                var always = Choice("ChatPermAlways", "2", alwaysLabel + (scoped ? " for " + Destination(item, destinationIndex) : ""), () => _ = AnswerPermission(item, true, remember: true, destination: DestinationKey(item), updated: EditedInput(item)));
                if (scoped) ToolTip.SetTip(always, item.Destinations[destinationIndex].Tooltip + " · Left or Right changes where this is saved");
                choices.Add(always);
            }
            choices.Add(Choice("ChatPermNo", choices.Count == 1 ? "2" : "3", "No", () => Reject(item)));
        }
        foreach (var choice in choices) root.Children.Add(choice);
        if (reasonBox is not null) root.Children.Add(reasonBox);
        root.Children.Add(Ui.Text("Esc to cancel", Ui.Hint, 11));
        root.KeyDown += (_, e) => PermissionKey(item, choices, e);
        return root;
    }

    private void AcpDetails(StackPanel root, ChatPermissionItem item)
    {
        if (item.ToolKind is { Length: > 0 } kind) root.Children.Add(Ui.Text(kind, Ui.Muted, 11));
        if (item.Locations.Count > 0)
        {
            var files = new SelectableTextBlock { Name = "ChatPermFiles", Text = string.Join("\n", item.Locations), TextWrapping = TextWrapping.Wrap, FontFamily = Ui.CodeFont, FontSize = 12, Foreground = Ui.Muted };
            root.Children.Add(files);
        }
        if (item.Description is { Length: > 0 } detail) root.Children.Add(new SelectableTextBlock { Name = "ChatPermNote", Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Muted });
        var facts = PermissionFacts.Of(item.Input);
        if (facts.Count > 0)
        {
            var box = new StackPanel { Name = "ChatPermFacts", Spacing = 2 };
            foreach (var (name, value) in facts)
            {
                box.Children.Add(Ui.Text(name, Ui.Hint, 11));
                box.Children.Add(new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.CodeFont, FontSize = 12, Foreground = Ui.TextBrush, MaxHeight = 160 });
            }
            root.Children.Add(box);
        }
        for (var at = 0; at < item.Diffs.Count; at++)
        {
            var diff = item.Diffs[at];
            var open = Ui.Button("Open diff", () => _ = env.OpenDiff(new ChatDiffRequest(Relative(diff.Path), _ => diff.NewText)));
            open.Name = "ChatPermDiff" + (at == 0 ? "" : "_" + at);
            root.Children.Add(Ui.Text(diff.Path + (diff.OldText is null ? " (new file)" : ""), Ui.Muted, 12));
            var lines = new SelectableTextBlock { Name = "ChatPermDiffText", Text = PermissionFacts.Unified(diff.OldText, diff.NewText), FontFamily = Ui.CodeFont, FontSize = 11, Foreground = Ui.TextBrush };
            root.Children.Add(new ScrollViewer { MaxHeight = 160, Content = lines, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            root.Children.Add(open);
        }
        // The whole raw input stays one fold away, selectable so it can be copied; it is folded because it can be long.
        if (item.Input.ValueKind == JsonValueKind.Object && item.Input.EnumerateObject().Any())
        {
            var json = JsonSerializer.Serialize(item.Input, ChatUIJson.Indented.JsonElement);
            root.Children.Add(new Expander { Name = "ChatPermRaw", Header = "Details", Content = new ScrollViewer { MaxHeight = 200, Content = new SelectableTextBlock { Text = json, FontFamily = Ui.CodeFont, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted } }, IsExpanded = false });
        }
    }

    private string? DestinationKey(ChatPermissionItem item) => destinationIndex >= 0 && destinationIndex < item.Destinations.Count ? item.Destinations[destinationIndex].Key : null;

    private JsonElement? EditedInput(ChatPermissionItem item)
    {
        if (commandBox is null || item.Input.ValueKind != JsonValueKind.Object) return null;
        var node = JsonNode.Parse(item.Input.GetRawText())!.AsObject();
        node["command"] = commandBox.Text ?? "";
        return JsonSerializer.SerializeToElement(node, ChatUIJson.Default.JsonObject);
    }

    private void Reject(ChatPermissionItem item)
    {
        var reason = reasonBox?.Text?.Trim();
        List<ChatPlanComment>? comments = null;
        if (item.ToolName == "ExitPlanMode")
        {
            comments = [.. planQuotes];
            if (!string.IsNullOrEmpty(reason)) comments.Add(new ChatPlanComment("", reason));
            if (comments.Count == 0) comments = null;
        }
        _ = AnswerPermission(item, false, reason: comments is null && !string.IsNullOrEmpty(reason) ? reason : null, comments: comments);
    }

    private void PermissionKey(ChatPermissionItem item, List<Button> choices, KeyEventArgs e)
    {
        var typing = e.Source is TextBox;
        if (e.Key == Key.Escape) { Reject(item); e.Handled = true; return; }
        if (typing) return;
        if (e.Key is >= Key.D1 and <= Key.D9 && e.Key - Key.D1 < choices.Count) { choices[e.Key - Key.D1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        else if (e.Key is >= Key.NumPad1 and <= Key.NumPad9 && e.Key - Key.NumPad1 < choices.Count) { choices[e.Key - Key.NumPad1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        else if (e.Key == Key.Enter) { (choices.FirstOrDefault(choice => choice.IsFocused) ?? choices[0]).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up)
        {
            var at = choices.FindIndex(choice => choice.IsFocused);
            choices[Math.Clamp(at + (e.Key == Key.Down ? 1 : -1), 0, choices.Count - 1)].Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right && item.AlwaysLabel is not null && item.Destinations.Count > 1)
        {
            CycleDestination(item, e.Key == Key.Right ? 1 : -1);
            e.Handled = true;
        }
    }

    internal void CycleDestination(ChatPermissionItem item, int step)
    {
        if (item.Destinations.Count < 2) return;
        destinationIndex = (Math.Max(0, destinationIndex) + step + item.Destinations.Count) % item.Destinations.Count;
        SaveComposer(Composer with { Destination = item.Destinations[destinationIndex].Key });
        UpdateRequests(true);
    }

    internal async Task AnswerPermission(ChatPermissionItem item, bool allow, bool remember = false, string? destination = null, string? reason = null, string? planMode = null,
        JsonElement? updated = null, IReadOnlyList<ChatPlanComment>? comments = null, string? optionId = null)
    {
        if (item.Decision != ChatPermissionDecision.Pending || !answering.Add(item.RequestId)) return;
        planQuotes.Clear();
        try
        {
            await env.Client.RequestAsync(ChatContract.Answer, new ChatAnswerParams(workspaceId, chatId, item.RequestId, allow)
            {
                Remember = remember ? true : null,
                Destination = destination,
                Reason = reason,
                PlanMode = planMode,
                UpdatedInput = updated,
                PlanComments = comments,
                OptionId = optionId
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            answering.Remove(item.RequestId);
            env.Notify(PluginNotificationKind.Error, "Couldn't answer the permission request", ChatErrors.Text(error));
        }
    }

    // ---- AskUserQuestion

    private Control BuildQuestion(ChatQuestionItem item)
    {
        var draft = transcript.DraftOf(item);
        questionTab = Math.Clamp(questionTab, 0, item.Questions.Count - 1);
        var root = new StackPanel { Name = "ChatQuestion", Spacing = 6, Focusable = true };
        var refs = new QuestionRefs(item.Id, root);
        var head = new DockPanel();
        var fold = Ui.IconButton(requestFolded ? "arrowRight" : "arrowDown", requestFolded ? "Show the questions" : "Fold the questions", () => { requestFolded = !requestFolded; UpdateRequests(true); });
        fold.Name = "ChatRequestFold";
        DockPanel.SetDock(fold, Dock.Right);
        head.Children.Add(fold);
        head.Children.Add(new SelectableTextBlock { Name = "ChatRequestTitle", Text = item.Questions.Count == 1 ? "Question" : "Questions", FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBrush, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(head);
        if (requestFolded) return root;
        if (item.Questions.Count > 1)
        {
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            for (var at = 0; at < item.Questions.Count; at++)
            {
                var index = at;
                var answered = draft.IsAnswered(at);
                var tabTitle = item.Questions[at].Header ?? "Question " + (at + 1);
                var tabText = SelectableLine((answered ? "✓ " : "○ ") + tabTitle, at == questionTab ? Ui.TextBrush : Ui.Muted, 12);
                refs.Tabs.Add((tabText, tabTitle));
                var tab = new Button
                {
                    Name = "ChatQuestionTab_" + at,
                    Content = tabText,
                    Padding = new Thickness(8, 2),
                    MinHeight = 0,
                    Background = at == questionTab ? Ui.Hover : Brushes.Transparent,
                    BorderThickness = new Thickness(0)
                };
                tab.Click += (_, _) => { questionTab = index; UpdateRequests(true); };
                ClickThroughText(tab);
                tabs.Children.Add(tab);
            }
            root.Children.Add(tabs);
        }
        var question = item.Questions[questionTab];
        root.Children.Add(new SelectableTextBlock { Name = "ChatQuestionText", Text = question.Question, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBrush });
        // A single-select question where an option carries a preview gets a pane beside the options: it shows the option under the pointer
        // after a 150 ms hover (the chosen one, else the first, until then), and says so when that option has none.
        var previewing = !question.MultiSelect && question.Options.Any(option => !string.IsNullOrEmpty(option.Preview));
        var optionList = new StackPanel();
        Border? pane = null;
        if (previewing)
        {
            var start = question.Options.FirstOrDefault(option => draft.Chosen[questionTab].Contains(option.Label)) ?? question.Options[0];
            pane = new Border { Name = "ChatQuestionPreview", Background = ChatPopup.Surface, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = ChatPopup.ContainerPadding, MinWidth = 240, MaxHeight = 240, Margin = new Thickness(8, 0, 0, 0), Child = PreviewBody(start) };
        }
        foreach (var option in question.Options)
        {
            var chosen = draft.Chosen[questionTab].Contains(option.Label);
            var mark = new Border { Width = 12, Height = 12, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(question.MultiSelect ? 3 : 6), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
            PaintMark(mark, chosen);
            refs.Options.Add((option.Label, mark));
            var text = new StackPanel();
            text.Children.Add(SelectableLine(option.Label, Ui.TextBrush, 12));
            if (option.Description is { Length: > 0 } hint) text.Children.Add(new SelectableTextBlock { Text = hint, FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(text, 1);
            body.Children.Add(mark);
            body.Children.Add(text);
            var button = new Button { Name = "ChatQuestionOption", Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3), MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            var label = option.Label;
            button.Click += (_, _) => QuestionPick(item, questionTab, label);
            ClickThroughText(button);
            if (pane is not null)
            {
                var target = pane;
                button.PointerEntered += (_, _) =>
                {
                    previewTimer?.Stop();
                    previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    previewTimer.Tick += (_, _) => { previewTimer?.Stop(); target.Child = PreviewBody(option); };
                    previewTimer.Start();
                };
                button.PointerExited += (_, _) => previewTimer?.Stop();
            }
            optionList.Children.Add(button);
        }
        if (pane is null) root.Children.Add(optionList);
        else
        {
            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1.2*") };
            Grid.SetColumn(pane, 1);
            split.Children.Add(optionList);
            split.Children.Add(pane);
            root.Children.Add(split);
        }
        var other = new TextBox { Name = "ChatQuestionOther", PlaceholderText = "Type your answer…", Text = draft.Other[questionTab], FontSize = 12 };
        var tabNow = questionTab;
        other.TextChanged += (_, _) => { if ((other.Text ?? "") != transcript.DraftOf(item).Other[tabNow]) QuestionOther(item, tabNow, other.Text ?? ""); };
        root.Children.Add(other);
        refs.Other = other;
        var submit = Choice("ChatQuestionSubmit", "", "Submit answers", () => SubmitQuestion(item), true);
        refs.Submit = submit;
        submit.IsEnabled = draft.Complete;
        var cancel = Choice("ChatQuestionCancel", "", "No", () => CancelQuestion(item));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(submit); buttons.Children.Add(cancel);
        var submitHint = new SelectableTextBlock { Name = "ChatQuestionHint", Text = SubmitHint(item, draft), FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(submitHint);
        refs.Hint = submitHint;
        // A disabled button takes no click, so the press is caught on the row: on Submit while it is disabled, it jumps to the first unanswered question.
        buttons.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!submit.IsEnabled && e.Source is Visual source && (ReferenceEquals(source, submit) || source.GetVisualAncestors().Contains(submit))) JumpToUnanswered(item);
        }, handledEventsToo: true);
        root.Children.Add(buttons);
        root.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelQuestion(item); e.Handled = true; }
            else if (e.Source is not TextBox && e.Key is Key.Left or Key.Right && item.Questions.Count > 1) { questionTab = Math.Clamp(questionTab + (e.Key == Key.Right ? 1 : -1), 0, item.Questions.Count - 1); UpdateRequests(true); e.Handled = true; }
        };
        questionRefs = refs;
        return root;
    }

    private DispatcherTimer? previewTimer;

    private static SelectableTextBlock SelectableLine(string text, IBrush brush, double size) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = size,
        FontWeight = Ui.InterfaceWeight,
        VerticalAlignment = VerticalAlignment.Center
    };

    // Selectable text inside a button takes the press and the capture, so the button never clicks. A press and release on such text
    // that did not move and did not click the button itself (a double click selects a word instead) clicks it here.
    private static void ClickThroughText(Button button)
    {
        Point? pressed = null;
        var fired = false;
        button.Click += (_, _) => fired = true;
        button.AddHandler(PointerPressedEvent, (_, e) =>
        {
            fired = false;
            pressed = e.Source is SelectableTextBlock && e.ClickCount == 1 && e.GetCurrentPoint(button).Properties.IsLeftButtonPressed ? e.GetPosition(button) : null;
        }, handledEventsToo: true);
        button.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            var start = pressed;
            pressed = null;
            if (start is not { } from || fired || !button.IsEnabled) return;
            var to = e.GetPosition(button);
            if (Math.Abs(to.X - from.X) < 4 && Math.Abs(to.Y - from.Y) < 4) button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }, handledEventsToo: true);
    }

    // What a question panel needs to change in place when an option is picked: a pick repaints marks and enables Submit and rebuilds nothing.
    private sealed class QuestionRefs(string itemId, Control root)
    {
        public string ItemId { get; } = itemId;
        public Control Root { get; } = root;
        public List<(string Label, Border Mark)> Options { get; } = [];
        public List<(TextBlock Text, string Title)> Tabs { get; } = [];
        public Button? Submit { get; set; }
        public TextBlock? Hint { get; set; }
        public TextBox? Other { get; set; }
    }

    private QuestionRefs? questionRefs;
    internal object? QuestionPanelInstance => requestPanel.Child;

    // Selection is colour only: the mark is the same size, border and inset chosen or not, so nothing moves.
    private static void PaintMark(Border mark, bool chosen)
    {
        mark.Background = chosen ? Ui.Accent : Brushes.Transparent;
        mark.BorderBrush = chosen ? Ui.Accent : Ui.Muted;
    }

    private void RefreshQuestion(ChatQuestionItem item)
    {
        if (questionRefs is not { } refs || refs.ItemId != item.Id || !ReferenceEquals(requestPanel.Child, refs.Root)) { UpdateRequests(true); return; }
        var draft = transcript.DraftOf(item);
        foreach (var (label, mark) in refs.Options) PaintMark(mark, draft.Chosen[questionTab].Contains(label));
        for (var at = 0; at < refs.Tabs.Count; at++)
        {
            refs.Tabs[at].Text.Text = (draft.IsAnswered(at) ? "✓ " : "○ ") + refs.Tabs[at].Title;
        }
        if (refs.Submit is { } submit) submit.IsEnabled = draft.Complete;
        if (refs.Hint is { } hint) hint.Text = SubmitHint(item, draft);
        if (refs.Other is { } other && (other.Text ?? "") != draft.Other[questionTab]) other.Text = draft.Other[questionTab];
        requestSignature = RequestSignature(item);
    }

    private static Control PreviewBody(ChatQuestionOption option) => string.IsNullOrEmpty(option.Preview)
        ? Ui.Text("No preview for this option", Ui.Muted, 12)
        : new Tereditor.Plugins.UI.Kit.Markdown.MarkdownPreview(option.Preview, "", new Tereditor.Plugins.UI.Kit.Markdown.MarkdownContext(
            (_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 12, 80, false))
        { MaxHeight = 224 };

    internal void QuestionPick(ChatQuestionItem item, int at, string label)
    {
        var multi = item.Questions[at].MultiSelect;
        transcript.SetDraft(item, transcript.DraftOf(item).Pick(at, label, multi));
        RefreshQuestion(item);
        // Choosing one answer moves on to the next question after a moment.
        if (multi || at >= item.Questions.Count - 1 || !transcript.DraftOf(item).Chosen[at].Contains(label)) return;
        advance?.Stop();
        advance = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        advance.Tick += (_, _) => { advance!.Stop(); if (questionTab == at) { questionTab = at + 1; UpdateRequests(true); } };
        advance.Start();
    }

    internal void QuestionOther(ChatQuestionItem item, int at, string text)
    {
        transcript.SetDraft(item, transcript.DraftOf(item).WithOther(at, item.Questions[at].MultiSelect, text));
        RefreshQuestion(item);
    }

    private static string SubmitHint(ChatQuestionItem item, QuestionDraft draft)
    {
        var titles = draft.Unanswered.Select(at => item.Questions.Count == 1 ? "the question" : item.Questions[at].Header ?? "Question " + (at + 1)).ToList();
        return titles.Count == 0 ? "" : "Answer " + (titles.Count == 1 ? titles[0] : string.Join(", ", titles.Take(titles.Count - 1)) + " and " + titles[^1]) + " to submit";
    }

    internal void JumpToUnanswered(ChatQuestionItem item)
    {
        if (transcript.DraftOf(item).Unanswered is not [var first, ..]) return;
        questionTab = first;
        UpdateRequests(true);
    }

    internal void SubmitQuestion(ChatQuestionItem item)
    {
        var draft = transcript.DraftOf(item);
        if (draft.Complete) _ = AnswerQuestion(item, draft.Answers(item), false);
    }

    internal void CancelQuestion(ChatQuestionItem item) => _ = AnswerQuestion(item, null, true);

    internal async Task AnswerQuestion(ChatQuestionItem question, IReadOnlyDictionary<string, string>? answers, bool cancelled)
    {
        if (!answering.Add(question.RequestId)) return;
        try
        {
            await env.Client.RequestAsync(ChatContract.AnswerQuestion, new ChatAnswerQuestionParams(workspaceId, chatId, question.RequestId) { Answers = answers, Cancelled = cancelled ? true : null });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            answering.Remove(question.RequestId);
            env.Notify(PluginNotificationKind.Error, "Couldn't answer the questions", ChatErrors.Text(error));
        }
    }

    // A failure card's Try again sends the last prompt once more, as upstream's recovery does.
    internal async Task RetryLast()
    {
        var last = transcript.AllItems().OfType<ChatUserItem>().OrderByDescending(item => item.Seq).FirstOrDefault(item => !item.Queued);
        if (last is null) return;
        await SendText(last.Text, last.Attachments);
    }
}

/// <summary>What a permission card says of an ACP call: the readable facts of its raw input, and a short diff.</summary>
internal static class PermissionFacts
{
    private static readonly (string Name, string[] Keys)[] Known =
    [
        ("Command", ["command", "cmd"]),
        ("Path", ["path", "file_path", "filePath", "filename"]),
        ("Pattern", ["pattern", "query"]),
        ("URL", ["url"]),
        ("Description", ["description"])
    ];

    /// <summary>The command, path, pattern, URL and description of the raw input, each as text; an argument list reads as one command line.</summary>
    internal static IReadOnlyList<(string Name, string Value)> Of(JsonElement input)
    {
        var facts = new List<(string, string)>();
        if (input.ValueKind != JsonValueKind.Object) return facts;
        foreach (var (name, keys) in Known)
        {
            foreach (var key in keys)
            {
                if (!input.TryGetProperty(key, out var value)) continue;
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "",
                    JsonValueKind.Array => string.Join(" ", value.EnumerateArray().Select(part => part.ValueKind == JsonValueKind.String ? part.GetString() : part.GetRawText())),
                    _ => ""
                };
                if (text.Length == 0) continue;
                facts.Add((name, text));
                break;
            }
        }
        return facts;
    }

    /// <summary>The lines that differ, with two lines of context: the text both sides share at the start and the end is cut, so a one-line change in a long file stays short.</summary>
    internal static string Unified(string? oldText, string newText, int limit = 80)
    {
        var before = string.IsNullOrEmpty(oldText) ? [] : oldText.Split('\n');
        var after = newText.Split('\n');
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
        var end = 0;
        while (end < before.Length - start && end < after.Length - start && before[^(end + 1)] == after[^(end + 1)]) end++;
        var lines = new List<string>();
        lines.AddRange(before[Math.Max(0, start - 2)..start].Select(line => "  " + line));
        lines.AddRange(before[start..(before.Length - end)].Select(line => "- " + line));
        lines.AddRange(after[start..(after.Length - end)].Select(line => "+ " + line));
        lines.AddRange(after[(after.Length - end)..Math.Min(after.Length, after.Length - end + 2)].Select(line => "  " + line));
        return lines.Count <= limit ? string.Join("\n", lines) : string.Join("\n", lines.Take(limit)) + $"\n\u2026 {lines.Count - limit} more lines";
    }
}