using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.UI.Rendering;
using Tereditor.UI.State;

namespace Tereditor.UI;

public sealed partial class WorkbenchWindow
{
    private readonly DispatcherTimer notificationTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>This window's toasts: receipts, refusals and results that need no decision.</summary>
    public ToastQueue Toasts { get; } = new();

    internal void ShowNotification(string message)
    {
        this.FindControl<TextBlock>("GestureToastMessage")!.Text = message;
        var toast = this.FindControl<Border>("GestureToast")!;
        // A box shadow bakes its colour, so it is read when the toast appears rather than bound.
        toast.BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 16, Color = Ui.PopoverShadow.Color });
        notificationTimer.Stop(); toast.IsVisible = true; notificationTimer.Start();
    }

    /// <summary>Shows a guarded UI failure as one toast with the log and a copy of the details.</summary>
    internal void ShowUiError(UiErrorNotice notice)
    {
        var message = notice.Many ? "Many errors happened. The app kept running. Details are in the log."
            : $"Something went wrong in {notice.Area}. The app kept running. Details are in the log.";
        Toasts.Push(ToastVariant.Error, message, "Something went wrong", TimeSpan.FromSeconds(15),
            actions: [new ToastAction("Copy details", () => CopyText(notice.Details, "Details copied.")),
                new ToastAction("Open log", () => _ = Task.Run(() =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UiErrorGuard.Shared.LogPath) { UseShellExecute = true }); }
                    catch (Exception error) { UiErrorGuard.Shared.Plugin("log opener", error); }
                }))]);
    }

    private void WireGestureNotification()
    {
        var toast = this.FindControl<Border>("GestureToast")!;
        var dismiss = this.FindControl<Button>("DismissGestureToast")!;
        var stack = this.FindControl<ToastStack>("ToastStack")!;
        stack.Attach(Toasts);
        void Appearance() => stack.Width = toast.Width = Bounds.Width < 640 ? Math.Max(0, Bounds.Width - 24) : 356;
        SizeChanged += (_, _) => Appearance();
        Appearance();
        void Hide() { notificationTimer.Stop(); toast.IsVisible = false; }
        notificationTimer.Tick += (_, _) => Hide();
        dismiss.Content = Ui.Icon("close", Ui.Muted, 14);
        dismiss.Click += (_, _) => Hide();
        surface.GestureCanceled += () => ShowNotification("The layout changed. Your drag was canceled.");
        Closed += (_, _) => { notificationTimer.Stop(); stack.Detach(); };
    }
}