using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Runtimes.Host;

/// <summary>
/// Asks the user once per runtime before a plugin's request downloads it. Requests that arrive while the question is open share it. A No is kept until
/// tereditor restarts or the user presses Download now, so a runtime is never asked about twice in a session. Nothing is stored on disk.
/// </summary>
internal sealed class RuntimeConsentBroker(Action<RuntimeConsents> publish)
{
    private readonly object gate = new();
    private readonly Dictionary<RuntimeKind, (RuntimeAsk Ask, TaskCompletionSource<bool> Answer)> pending = [];
    private readonly HashSet<RuntimeKind> declined = [];

    internal static string Id(RuntimeKind kind) => kind == RuntimeKind.Bun ? "bun" : "uv";

    internal async ValueTask<bool> AskAsync(RuntimeOffer offer, CancellationToken token)
    {
        TaskCompletionSource<bool> answer;
        RuntimeConsents? changed = null;
        lock (gate)
        {
            if (declined.Contains(offer.Kind)) return false;
            if (!pending.TryGetValue(offer.Kind, out var open))
            {
                open = (new RuntimeAsk(Id(offer.Kind), offer.Name, offer.Version), new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                pending[offer.Kind] = open;
                changed = Snapshot();
            }
            answer = open.Answer;
        }
        if (changed is not null) publish(changed);
        return await answer.Task.WaitAsync(token);
    }

    internal RuntimeConsents Snapshot()
    {
        lock (gate) return new([.. pending.Values.Select(open => open.Ask)]);
    }

    internal RuntimeConsents Answer(RuntimeKind kind, bool download)
    {
        TaskCompletionSource<bool>? answer = null;
        lock (gate)
        {
            if (pending.Remove(kind, out var open)) answer = open.Answer;
            if (!download) declined.Add(kind);
        }
        var now = Snapshot();
        answer?.TrySetResult(download);
        publish(now);
        return now;
    }

    /// <summary>The user asked for the download themselves, so the earlier Not now is over.</summary>
    internal void Forget(RuntimeKind kind)
    {
        lock (gate) declined.Remove(kind);
    }

    /// <summary>The plugin stops: every open question ends as Not now, and nothing is remembered.</summary>
    internal void Close()
    {
        List<TaskCompletionSource<bool>> open;
        lock (gate)
        {
            open = [.. pending.Values.Select(item => item.Answer)];
            pending.Clear();
            declined.Clear();
        }
        foreach (var answer in open) answer.TrySetResult(false);
    }
}