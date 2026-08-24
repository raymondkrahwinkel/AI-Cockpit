namespace Cockpit.Core.Abstractions.Agents;

// AC-1013: The two things one agent can spend on the line (AC-396), counted apart because they cost different
// people different amounts — a shared cap would be either loose enough to let a wake loop through or too tight to talk.
public enum AgentLineActivity
{
    // An accepted `notify` — a message put in a neighbour's inbox.
    Message,

    // A wake actually attempted on a neighbour (AC-395), after its consent was found and the message was accepted.
    Wake,
}

// AC-1013: What the budget said about one attempt, with the numbers to act on it. Refusals are informative and
// temporary by design — a guard rail against a loop, not a punishment (Raymond, 2026-07-28: "dat zijn meer guard
// rails"). RetryAfter is a duration, not a timestamp, since "how long" is what the sender needs, not "until when".
public sealed record AgentLineBudgetVerdict(
    bool Allowed,
    AgentLineActivity Activity,
    int Used,
    int Limit,
    TimeSpan Window,
    TimeSpan RetryAfter);

// AC-1013: One pane's standing against one of the two counters, for the operator-facing read.
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
