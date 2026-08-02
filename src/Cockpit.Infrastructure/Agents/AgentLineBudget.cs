using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// The concrete rate limit behind <see cref="IAgentLineBudget"/> (AC-396): one sliding window per (sender, activity),
/// holding the moments that are still inside it.
/// <para>
/// A sliding window rather than a counter reset on the hour, because a fixed window lets a sender spend its whole
/// allowance at the end of one and the whole of the next immediately after — twice the limit back to back, which is
/// precisely the burst this exists to catch. The queue costs no more than the counter would: it never grows past
/// <see cref="MaxMessagesPerWindow"/> entries, since that is the point at which nothing more is added.
/// </para>
/// <para>
/// Everything is behind one lock, like the inbox and the claims: charging is a check-then-act (is there room, and
/// take it) and two MCP request threads for the same pane would otherwise both find the last slot free.
/// </para>
/// <para>
/// <strong>The numbers are constructor parameters, not constants read at the use site.</strong> Raymond asked for a
/// defensive default that can be adjusted, because the right value is not knowable in advance. What is not built here
/// is a settings surface for the operator to turn them from — that hangs off one registration and is deliberately
/// left until there is a reason to move them.
/// </para>
/// </summary>
internal sealed class AgentLineBudget : IAgentLineBudget, ISingletonService
{
    /// <summary>
    /// Messages one pane may send inside <see cref="DefaultWindow"/>. Generous against real use — an agent that tells
    /// its neighbours what it is doing sends a handful over a session, not twenty a minute — and still tight enough
    /// that a loop hits it within seconds rather than after the damage.
    /// </summary>
    internal const int MaxMessagesPerWindow = 20;

    /// <summary>
    /// Wakes one pane may attempt inside <see cref="DefaultWindow"/>. Far lower than the message cap because the two
    /// cost different things: a message waits, a wake spends a turn belonging to somebody else's operator. Five in a
    /// minute is already more urgency than any real situation has.
    /// </summary>
    internal const int MaxWakesPerWindow = 5;

    /// <summary>
    /// How far back a count reaches. A minute is short enough that a sender refused once is sending again almost
    /// immediately, which is what keeps this a guard rail rather than a lockout.
    /// </summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();

    // Append-ordered per key, so the oldest attempt — the one whose expiry decides RetryAfter — is always at the head
    // and no sort is needed anywhere.
    private readonly Dictionary<(string PaneId, AgentLineActivity Activity), Queue<DateTimeOffset>> _spent = [];

    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly int _messagesPerWindow;
    private readonly int _wakesPerWindow;

    /// <summary>The registered shape: the wall clock and the defaults above.</summary>
    public AgentLineBudget()
        : this(TimeProvider.System, DefaultWindow, MaxMessagesPerWindow, MaxWakesPerWindow)
    {
    }

    /// <summary>
    /// For tests, which need to move time rather than wait out a window, and to state a small limit instead of
    /// sending twenty messages to reach the interesting case.
    /// </summary>
    internal AgentLineBudget(TimeProvider time, TimeSpan window, int messagesPerWindow, int wakesPerWindow)
    {
        _time = time;
        _window = window;
        _messagesPerWindow = messagesPerWindow;
        _wakesPerWindow = wakesPerWindow;
    }

    public AgentLineBudgetVerdict Charge(string paneId, AgentLineActivity activity)
    {
        var limit = _Limit(activity);

        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var spent = _Inside(paneId, activity, now);

            if (spent.Count >= limit)
            {
                // The oldest attempt still counted is the one whose expiry frees a slot. Clamped at zero because the
                // two ends are separate clock reads and an OS that steps the clock forward between them would
                // otherwise hand back a negative wait, which reads as nonsense exactly where a number is meant to
                // tell an agent how long to hold off.
                var retryAfter = spent.Peek() + _window - now;
                return new AgentLineBudgetVerdict(
                    Allowed: false,
                    activity,
                    spent.Count,
                    limit,
                    _window,
                    retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero);
            }

            spent.Enqueue(now);
            return new AgentLineBudgetVerdict(Allowed: true, activity, spent.Count, limit, _window, TimeSpan.Zero);
        }
    }

    public IReadOnlyList<AgentLineBudgetUsage> Usage()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();

            // Read through the same expiry the charge path applies, so the operator is shown what is actually being
            // held against a sender right now and not what it spent ten minutes ago. Empty counters are left out:
            // a pane that sent something once and has been quiet since is not news.
            return
            [
                .. _spent
                    .Select(entry => (entry.Key, Inside: _Expire(entry.Value, now)))
                    .Where(entry => entry.Inside > 0)
                    .Select(entry => new AgentLineBudgetUsage(
                        entry.Key.PaneId,
                        entry.Key.Activity,
                        entry.Inside,
                        _Limit(entry.Key.Activity),
                        _window)),
            ];
        }
    }

    public void Forget(string paneId)
    {
        lock (_lock)
        {
            // Both counters named rather than the dictionary scanned: there are exactly two, and naming them keeps a
            // third activity added later from being silently left behind by a filter nobody re-read.
            _spent.Remove((paneId, AgentLineActivity.Message));
            _spent.Remove((paneId, AgentLineActivity.Wake));
        }
    }

    private int _Limit(AgentLineActivity activity) =>
        activity == AgentLineActivity.Wake ? _wakesPerWindow : _messagesPerWindow;

    /// <summary>
    /// This pane's queue for this activity with everything older than the window dropped, creating it when the pane
    /// has never spent anything. Always called under the lock.
    /// </summary>
    private Queue<DateTimeOffset> _Inside(string paneId, AgentLineActivity activity, DateTimeOffset now)
    {
        var key = (paneId, activity);
        if (!_spent.TryGetValue(key, out var spent))
        {
            spent = new Queue<DateTimeOffset>();
            _spent[key] = spent;
        }

        _Expire(spent, now);
        return spent;
    }

    /// <summary>Drops everything that has fallen out of the window and answers with what is left. Always called under the lock.</summary>
    private int _Expire(Queue<DateTimeOffset> spent, DateTimeOffset now)
    {
        while (spent.Count > 0 && spent.Peek() + _window <= now)
        {
            spent.Dequeue();
        }

        return spent.Count;
    }
}
