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
/// The rate at which one agent session may use the line (AC-396), keeping polite replies from becoming a loop that
/// spends a desk's turns. A rate over a window, not a session total (Raymond: catching a runaway, not thrift), charged
/// to the sender alone, unlike the old per-recipient <c>MaxWaitingPerPane</c> that let a looping neighbour get every sender refused (AC-119 S10). Keyed on pane id only; runtime-only, refusals go to the notify trail.
/// </summary>
public interface IAgentLineBudget
{
    /// <summary>
    /// Asks whether <paramref name="paneId"/> may spend one <paramref name="activity"/> now, counting it only when
    /// allowed — an unwelcome refusal is never counted, or a rate limit would turn into a lockout. Callers must
    /// charge only what actually happened: an attempt the host refused on its own account is not the sender's quota.
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
