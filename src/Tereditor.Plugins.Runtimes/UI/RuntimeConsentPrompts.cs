using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Runtimes.UI;

/// <summary>
/// One prompt per download that waits for an answer, in every window. The list comes from the host, so every client shows the same prompts and one answer
/// takes them all down. Download starts the download (the host does it; the page shows progress), Not now keeps it away until tereditor restarts.
/// </summary>
internal sealed class RuntimeConsentPrompts(IPluginUIContext context) : IDisposable
{
    private readonly Dictionary<string, IDisposable> shown = [];

    internal int Shown => shown.Count;

    internal void Apply(RuntimeConsents consents)
    {
        foreach (var kind in shown.Keys.Where(kind => consents.Pending.All(ask => ask.Kind != kind)).ToArray())
        {
            shown[kind].Dispose();
            shown.Remove(kind);
        }
        foreach (var ask in consents.Pending.Where(ask => !shown.ContainsKey(ask.Kind)))
            shown[ask.Kind] = context.Prompt(new PluginPrompt(PluginNotificationKind.Info, Message(ask), [
                new("Download", () => AnswerAsync(ask, true), PluginNotificationActionStyle.Primary),
                new("Not now", () => AnswerAsync(ask, false))], "Download " + ask.Name + "?"));
    }

    internal static string Message(RuntimeAsk ask) =>
        $"A plugin needs {ask.Name} {ask.Version} to run. It is not on this computer. tereditor can download it now. Nothing is downloaded until you say yes.";

    private async ValueTask AnswerAsync(RuntimeAsk ask, bool download) =>
        Apply(await context.RequestAsync(RuntimesContract.Answer, new RuntimeAnswerParams(ask.Kind, download)));

    public void Dispose()
    {
        foreach (var prompt in shown.Values) prompt.Dispose();
        shown.Clear();
    }
}