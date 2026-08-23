using Cockpit.Core.Abstractions;

namespace Cockpit.Core.Sessions;

// AC-408: default `ISessionConversationSink`, keeping the latest reported conversation id per pane and
// raising `Changed` only when it actually differs. Deliberately just the pass-through point — no
// persistence, no resume offer; both are a follow-up ticket's concern.
public sealed class SessionConversationTracker : ISessionConversationSink, ISingletonService
{
    private readonly Dictionary<string, SessionConversationId> _known = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    // Raised once per pane whenever its conversation id actually changes.
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
