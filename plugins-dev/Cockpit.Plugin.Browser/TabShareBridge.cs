using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using static Cockpit.Plugin.Browser.TabShareContract;

namespace Cockpit.Plugin.Browser;

// AC-1714: asks the UI half for a shared tab on an agent's behalf and waits for its answer. No UI attached, or no
// answer in time, is a refusal: an unanswered read never turns into one that went ahead.
internal sealed class TabShareBridge : IDisposable
{
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(15);

    private readonly ICockpitHost _host;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TabShareAnswer>> _pending = new();
    private readonly List<IDisposable> _handles = [];
    private readonly EventHandler<string> _sessionClosed;

    public TabShareBridge(ICockpitHost host)
    {
        _host = host;
        _handles.Add(host.Channel.Handle(Answer, (payload, _) =>
        {
            if (payload.Deserialize<TabShareAnswer>(Json) is { } answer && _pending.TryRemove(answer.Id, out var waiting))
            {
                waiting.TrySetResult(answer);
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(true, Json));
        }));
        _handles.Add(host.Channel.Handle(Sessions, (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(host.Sessions.OpenSessions, Json))));
        _sessionClosed = (_, paneId) => host.Channel.Publish(SessionClosed, JsonSerializer.SerializeToElement(paneId, Json));
        host.Sessions.SessionClosed += _sessionClosed;
    }

    public Task<TabShareAnswer> AskAsync(string paneId, string tool, string? tabId = null, string? mode = null) =>
        AskAsync(new TabShareRequest(Guid.NewGuid().ToString("N"), paneId, tool, tabId, mode), AnswerTimeout);

    // AC-1728: a request with its own deadline, for the control card that waits on the operator.
    public async Task<TabShareAnswer> AskAsync(TabShareRequest request, TimeSpan timeout)
    {
        var waiting = new TaskCompletionSource<TabShareAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = waiting;
        try
        {
            _host.Channel.Publish(Request, JsonSerializer.SerializeToElement(request, Json));
            return await waiting.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new TabShareAnswer(request.Id, false, "The browser did not answer. It has to be open in Cockpit on this machine.");
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    public void Dispose()
    {
        _host.Sessions.SessionClosed -= _sessionClosed;
        foreach (var handle in _handles)
        {
            handle.Dispose();
        }
    }
}
