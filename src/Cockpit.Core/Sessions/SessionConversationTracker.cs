using Cockpit.Core.Abstractions;

namespace Cockpit.Core.Sessions;

/// <summary>
/// The default <see cref="ISessionConversationSink"/> (AC-408): keeps the latest reported conversation id per pane
/// in memory and raises <see cref="Changed"/> only when a report actually differs from what that pane last
/// reported — a route that reports the same id on every session event must not turn into a change every time.
/// Deliberately just the pass-through point: no persistence and no resume offer, both of which are a follow-up
/// ticket's concern.
/// </summary>
public sealed class SessionConversationTracker : ISessionConversationSink, ISingletonService
{
    private readonly Dictionary<string, SessionConversationId> _known = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Raised once per pane whenever its conversation id actually changes.</summary>
    public event Action<SessionConversationReported>? Changed;

    public void Report(string paneId, SessionConversationId conversation)
    {
        lock (_gate)
        {
            if (_known.TryGetValue(paneId, out var existing) && existing == conversation)
            {
                return;
            }

            _known[paneId] = conversation;
        }

        Changed?.Invoke(new SessionConversationReported(paneId, conversation));
    }
}
