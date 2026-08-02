namespace Cockpit.Core.Abstractions.Agents;

// The two things one agent can spend on the line (AC-396), counted apart because they cost different people
// different amounts. A message waits in an inbox until its recipient chooses to read it; a wake starts a turn the
// recipient's operator pays for and did not ask for. One cap over both would either be loose enough to let a
// wake loop through or tight enough to stop ordinary talking.
public enum AgentLineActivity
{
    // An accepted `notify` — a message put in a neighbour's inbox.
    Message,

    // A wake actually attempted on a neighbour (AC-395), after its consent was found and the message was accepted.
    Wake,
}

// What the budget said about one attempt, with the numbers the sender needs to act on it. Refusals here are
// informative and temporary by design: this is a guard rail against a loop, not a punishment and not a kill switch
// for the sender — Raymond settled that on the ticket (2026-07-28: *"dat zijn meer guard rails"*).
//
// `Allowed`: Whether the attempt may go ahead. False means it was not counted either — a refused attempt does not deepen the hole it is in.
// `Activity`: Which of the two counters this verdict is about.
// `Used`: How many of this kind the pane has spent inside the window, counting this one when it was allowed.
// `Limit`: The most of this kind one pane may spend inside the window.
// `Window`: How far back the count reaches.
// `RetryAfter`:
// How long until the oldest counted attempt falls out of the window and room appears again — zero when the attempt
// was allowed. Given as a duration rather than a timestamp because it is what the sender actually needs: an agent
// deciding whether to wait or to take another route is asking "how long", not "until when".
public sealed record AgentLineBudgetVerdict(
    bool Allowed,
    AgentLineActivity Activity,
    int Used,
    int Limit,
    TimeSpan Window,
    TimeSpan RetryAfter);

// One pane's standing against one of the two counters, for the operator-facing read.
//
// `PaneId`: The sender the count belongs to.
// `Activity`: Which counter.
// `Used`: How many attempts of that kind are still inside the window.
// `Limit`: The cap that applies.
// `Window`: How far back the count reaches.
public sealed record AgentLineBudgetUsage(
    string PaneId,
    AgentLineActivity Activity,
    int Used,
    int Limit,
    TimeSpan Window);

/// <summary>
/// The rate at which one agent session may use the line (AC-396) — the guard rail that keeps two agents politely
/// answering each other from becoming a loop that spends a desk's turns.
/// <para>
/// <strong>A rate over a window, not a total for the session.</strong> Raymond settled this on the ticket: the point
/// is not thrift but catching a runaway. A lifetime total would eventually stop an agent that has done nothing wrong
/// except work for a long time, and would not stop a fast loop any sooner than a window does.
/// </para>
/// <para>
/// <strong>Charged to the sender, and only to the sender.</strong> The bound that existed before this
/// (<c>MaxWaitingPerPane</c>) is a total per <em>recipient</em> across every sender, so one looping neighbour fills a
/// recipient's inbox and every legitimate sender is then refused with <c>RefusedRecipientInboxFull</c> for something
/// it did not do. A cap on the sender stops the loop where it starts and leaves an uninvolved third party's message
/// untouched — which is the second half of AC-119's scenario S10, and the half a cap that only counted arrivals
/// would miss.
/// </para>
/// <para>
/// <strong>Keyed on pane id, which is what makes it per workspace.</strong> There is no workspace key here for the
/// same reason there is none in <see cref="IAgentMessageInbox"/> or <see cref="IAgentResourceClaims"/>: a pane's
/// workspace is derived per call and can change over that pane's life, so state filed under it can be looked for
/// under a different key later. Counting per sending pane gives the isolation the ticket asks for and gives it more
/// strongly — one desk's traffic cannot pause another's, because no pane's count is ever consulted for a different
/// pane at all.
/// </para>
/// <para>
/// Runtime state for the life of the app, like the inbox and the claims. The durable record of what was refused is
/// the append-only notify trail (<see cref="IAgentNotifyAuditLog"/>), which carries
/// <see cref="AgentNotifyOutcome.RefusedRateLimited"/> and is what the inspector (AC-397) reads back.
/// </para>
/// </summary>
public interface IAgentLineBudget
{
    /// <summary>
    /// Asks whether <paramref name="paneId"/> may spend one <paramref name="activity"/> now, and counts it when the
    /// answer is yes. A refused attempt is not counted: the window would otherwise never empty for a sender that
    /// keeps trying, turning a rate limit into a lockout, which is exactly what this is not meant to be.
    /// <para>
    /// Callers are expected to charge only what actually happened — an attempt the host refused on its own account
    /// (a pane that is not on the desk, a body over the limit) is not the sender's quota to spend, or one mistyped
    /// pane id would eat the budget an agent needs for the message it got right.
    /// </para>
    /// </summary>
    AgentLineBudgetVerdict Charge(string paneId, AgentLineActivity activity);

    /// <summary>
    /// Every pane with something still inside the window, for the operator's read (AC-397). Deliberately not
    /// reachable from any MCP tool: what a neighbour has been sending is the operator's business, not another
    /// agent's, and an agent that could read it would learn about traffic it is not part of.
    /// </summary>
    IReadOnlyList<AgentLineBudgetUsage> Usage();

    /// <summary>
    /// Drops what <paramref name="paneId"/> has spent — for a pane whose session has ended, so a closed session's
    /// counts do not sit in host memory for the life of the app. Idempotent; a pane that spent nothing is a no-op.
    /// </summary>
    void Forget(string paneId);
}
