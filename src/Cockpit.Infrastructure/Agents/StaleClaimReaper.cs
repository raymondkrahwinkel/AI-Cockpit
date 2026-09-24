using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Assistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Agents;

// AC-644: the crash net for claims that worktrees already have (AC-85/AC-643). `Forget` runs on every ordinary
// session close, so this only catches a crashed/killed session whose claims would otherwise keep warning neighbours
// off an unused worktree. A liveness check only: it asks whether the owner pane still exists, never claim age.
public sealed class StaleClaimReaper : ISingletonService, IDisposable
{
    // Same clock as the worktree net: far enough from a session that is mid-close, close enough that a crashed
    // agent's claim does not stand for the rest of the day.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // Who the message is from. Not a pane and never will be — the cockpit itself noticed this, not a neighbour.
    private const string SenderPaneId = "cockpit-claim-watch";

    private readonly IAgentResourceClaimsAudit _claimsAudit;
    private readonly IAgentResourceClaims _claims;
    private readonly IAgentMessageInbox _inbox;
    private readonly ILogger<StaleClaimReaper> _logger;
    private readonly TimeProvider _time;

    private ITimer? _timer;
    private bool _disposed;

    public StaleClaimReaper(
        IAgentResourceClaimsAudit claimsAudit,
        IAgentResourceClaims claims,
        IAgentMessageInbox inbox,
        ILogger<StaleClaimReaper>? logger = null)
        : this(claimsAudit, claims, inbox, logger, TimeProvider.System)
    {
    }

    // Test seam: a controllable clock, so a sweep is provable without waiting a quarter of an hour for it.
    internal StaleClaimReaper(
        IAgentResourceClaimsAudit claimsAudit,
        IAgentResourceClaims claims,
        IAgentMessageInbox inbox,
        ILogger<StaleClaimReaper>? logger,
        TimeProvider time)
    {
        _claimsAudit = claimsAudit;
        _claims = claims;
        _inbox = inbox;
        _logger = logger ?? NullLogger<StaleClaimReaper>.Instance;
        _time = time;
    }

    // The panes alive right now, asked fresh every tick: a claim owned by anything outside this set is stale. Set by
    // the cockpit, which owns the session list; nothing is reaped until it is.
    public Func<IReadOnlyCollection<string>>? LivePaneIds { get; set; }

    // Starts sweeping the clock. Idempotent. No sweep now: at startup the sessions being restored are not all back
    // yet, and a pane that has not landed reads exactly like a pane that crashed.
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = _time.CreateTimer(_ => _OnTick(), null, Interval, Interval);
    }

    // One sweep. Public because the tests drive it directly rather than waiting a quarter of an hour — the same seam
    // `CiWatcher.RunOnceAsync` opens. Synchronous throughout: reading the claim store and forgetting a pane are both
    // in-memory, so a sweep that finds nothing costs a dictionary walk and no process at all.
    public void RunOnce()
    {
        if (LivePaneIds is null)
        {
            return;
        }

        var live = LivePaneIds().ToHashSet(StringComparer.Ordinal);
        var stale = _claimsAudit.ListAll().Where(claim => !live.Contains(claim.OwnerPaneId));

        foreach (var group in stale.GroupBy(claim => claim.OwnerPaneId, StringComparer.Ordinal))
        {
            // Forgotten per pane, not per claim: `Forget` drops everything that pane holds in one call, so calling
            // it once per resource would report each of the later ones as if it had still been standing.
            _claims.Forget(group.Key);
            _Report(group.Key, [.. group.Select(claim => claim.Resource)]);
        }
    }

    private void _Report(string deadPaneId, IReadOnlyList<string> resources)
    {
        var named = string.Join(", ", resources);
        _logger.LogInformation("Forgot {Count} claim(s) of pane {PaneId}, which is no longer live: {Resources}.", resources.Count, deadPaneId, named);

        // The assistant, not the operator: it is usually the one that told the agent to claim this in the first
        // place, and the one placed to notice if the same pattern keeps happening.
        _inbox.Deliver(
            SenderPaneId,
            AssistantIdentity.PaneId,
            "claims",
            $"Pane '{deadPaneId}' is gone without releasing what it had claimed, so the cockpit has forgotten it: "
                + $"{named}. Nothing else has been started about it.");
    }

    private void _OnTick()
    {
        try
        {
            RunOnce();
        }
        catch (Exception exception)
        {
            // A sweep must never be the reason the cockpit falls over, but it must leave a trace — a failure that
            // stops the loop silently is a crash net that never catches anything again.
            _logger.LogError(exception, "A stale claim sweep failed; the next one will try again.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        LivePaneIds = null;
        _timer?.Dispose();
        _timer = null;
    }
}
