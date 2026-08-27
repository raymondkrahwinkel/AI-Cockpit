using System.Text;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// Order is never touched: only *adjacent* events merge, and only into the run's own first event, so the sequence handed
// to `Apply` is the arrival sequence with runs collapsed (AC-529).
internal static class SessionEventCoalescer
{
    // Returns `events` with adjacent mergeable deltas folded together, or the same instance when
    // there was nothing to fold.
    public static IReadOnlyList<SessionEvent> Coalesce(IReadOnlyList<SessionEvent> events)
    {
        if (events.Count < 2)
        {
            return events;
        }

        List<SessionEvent>? result = null;
        var index = 0;
        while (index < events.Count)
        {
            var head = events[index];
            var runEnd = index + 1;
            while (runEnd < events.Count && _Mergeable(head, events[runEnd]))
            {
                runEnd++;
            }

            if (runEnd - index == 1)
            {
                result?.Add(head);
                index = runEnd;
                continue;
            }

            // First fold in this batch: materialise the events kept so far, then carry on appending.
            if (result is null)
            {
                result = new List<SessionEvent>(events.Count);
                for (var kept = 0; kept < index; kept++)
                {
                    result.Add(events[kept]);
                }
            }

            // Built through a StringBuilder rather than by repeatedly merging pairs: pairwise merging of a run of
            // N deltas is the very quadratic copy this class exists to remove.
            var text = new StringBuilder();
            for (var member = index; member < runEnd; member++)
            {
                text.Append(_TextOf(events[member]));
            }

            result.Add(_WithText(head, text.ToString()));
            index = runEnd;
        }

        return result ?? events;
    }

    // Whether `next` can be folded into the run `head` started. Deliberately
    // strict: every field that decides which row a delta lands on must match, and so must the session it belongs to,
    // which leaves the merged event indistinguishable from its parts for every reader except `Uuid`.
    private static bool _Mergeable(SessionEvent head, SessionEvent next) => (head, next) switch
    {
        (AssistantTextDelta first, AssistantTextDelta second) =>
            first.BlockIndex == second.BlockIndex && _SameLane(first, second),
        (AssistantThinkingDelta first, AssistantThinkingDelta second) =>
            first.BlockIndex == second.BlockIndex && _SameLane(first, second),
        _ => false,
    };

    private static bool _SameLane(SessionEvent first, SessionEvent second) =>
        string.Equals(first.ParentToolUseId, second.ParentToolUseId, StringComparison.Ordinal)
        && string.Equals(first.SessionId, second.SessionId, StringComparison.Ordinal);

    private static string _TextOf(SessionEvent evt) => evt switch
    {
        AssistantTextDelta delta => delta.Text,
        AssistantThinkingDelta delta => delta.Thinking,
        _ => string.Empty,
    };

    private static SessionEvent _WithText(SessionEvent head, string text) => head switch
    {
        AssistantTextDelta delta => delta with { Text = text },
        AssistantThinkingDelta delta => delta with { Thinking = text },
        _ => head,
    };
}
