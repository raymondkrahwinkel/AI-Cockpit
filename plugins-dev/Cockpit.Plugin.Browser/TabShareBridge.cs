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
    private readonly Dictionary<string, (string PaneId, TaskCompletionSource<TabShareAnswer> Answer)> _pending = [];
    private readonly List<IDisposable> _handles = [];
    private readonly EventHandler<string> _sessionClosed;
    private readonly object _gate = new();
    private bool _disposed;

    public TabShareBridge(ICockpitHost host)
    {
        _host = host;
        _handles.Add(host.Channel.Handle(Cancel, (payload, _) =>
        {
            lock (_gate)
            {
                foreach (var id in payload.Deserialize<string[]>(Json) ?? [])
                {
                    if (_pending.Remove(id, out var waiting))
                    {
                        waiting.Answer.TrySetCanceled();
                    }
                }
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(true, Json));
        }));
        _handles.Add(host.Channel.Handle(Answer, (payload, _) =>
        {
            lock (_gate)
            {
                if (payload.Deserialize<TabShareAnswer>(Json) is { } answer && _pending.Remove(answer.Id, out var waiting))
                {
                    waiting.Answer.TrySetResult(answer);
                }
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(true, Json));
        }));
        _handles.Add(host.Channel.Handle(Sessions, (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(
            host.Sessions.OpenSessions.Where(session => host.Sessions.HasMcpServer(session.PaneId, "cockpit-browser")), Json))));
        _sessionClosed = (_, paneId) =>
        {
            lock (_gate)
            {
                foreach (var id in _pending.Where(pair => pair.Value.PaneId == paneId).Select(pair => pair.Key).ToArray())
                {
                    if (_pending.Remove(id, out var waiting))
                    {
                        waiting.Answer.TrySetCanceled();
                    }
                }
                host.Channel.Publish(SessionClosed, JsonSerializer.SerializeToElement(paneId, Json));
            }
        };
        host.Sessions.SessionClosed += _sessionClosed;
    }

    public Task<TabShareAnswer> AskAsync(string paneId, string tool, string? tabId = null, string? mode = null) =>
        AskAsync(new TabShareRequest(Guid.NewGuid().ToString("N"), paneId, tool, tabId, mode), AnswerTimeout);

    // AC-1728: a request with its own deadline, for the control card that waits on the operator.
    public async Task<TabShareAnswer> AskAsync(TabShareRequest request, TimeSpan timeout)
    {
        var waiting = new TaskCompletionSource<TabShareAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException();
                }
                _pending.Add(request.Id, (request.PaneId, waiting));
                _host.Channel.Publish(Request, JsonSerializer.SerializeToElement(request, Json));
            }
            return await waiting.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new TabShareAnswer(request.Id, false, "The browser did not answer. It has to be open in Cockpit on this machine.");
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(request.Id);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var waiting in _pending.Values)
            {
                waiting.Answer.TrySetCanceled();
            }
            _pending.Clear();
        }
        _host.Sessions.SessionClosed -= _sessionClosed;
        foreach (var handle in _handles)
        {
            handle.Dispose();
        }
    }
}
