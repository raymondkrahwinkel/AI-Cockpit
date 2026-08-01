namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// Which desk a spawn lands on, and — inseparably — <em>how that was decided</em> (AC-545).
/// </summary>
/// <remarks>
/// <b>Read this before adding a second caller.</b> One host-side spawn service serves two callers with two
/// different levels of authority, and the difference between them is not a check that runs after the target is
/// known — it is <em>how the target is arrived at in the first place</em>:
/// <list type="table">
/// <item>
///   <term>the assistant (AC-545)</term>
///   <description>names the workspace outright. It sits on no desk at all
///   (<c>SessionWorkspacePlacement</c> places it nowhere, by construction), so there is nothing to derive a target
///   from — and that is precisely why its spawns are confirmed out loud and on screen before anything starts.</description>
/// </item>
/// <item>
///   <term>a coordinator (AC-436, not built yet)</term>
///   <description>does not get to name anything. Its target is derived host-side from the
///   transport-verified <c>McpRequestContext.CurrentPaneId</c>, and its tool takes no workspace parameter at all, so
///   a coordinator cannot reach a desk that is not its own however it phrases the request.</description>
/// </item>
/// </list>
/// <para>
/// <b>The strict rule is a different rule, not a filter on the permissive one.</b> The assistant's rule landed
/// first, and the cheap way to add the coordinator's later is "the same call, plus a check that the workspace it
/// passed is its own". Do not. That shape keeps a parameter on the wire that the coordinator's whole guarantee is
/// that it does not have, and it makes the guardrail a validation someone can weaken, relax for one caller, or
/// forget on the next tool — instead of an absence the caller cannot argue with. AC-436's resolver must read the
/// caller's pane and construct <see cref="DerivedFromTheCallersPane"/> itself; it must never accept a workspace id
/// from its caller and hand it to <see cref="NamedByTheAssistant"/>.
/// </para>
/// <para>
/// <b>Which is why this type has no public constructor.</b> The two factories below are the only two doors, they
/// are named after the two rules rather than after their arguments, and each one stamps who came through it — so
/// the audit trail records the authority a spawn was made under and not merely the desk it landed on. A third
/// caller with a third rule adds a third factory here, next to these, where the comparison is unavoidable.
/// </para>
/// </remarks>
public sealed record SpawnTarget
{
    private SpawnTarget(string workspaceId, SpawnCaller caller, string? callerPaneId)
    {
        WorkspaceId = workspaceId;
        Caller = caller;
        CallerPaneId = callerPaneId;
    }

    /// <summary>The desk the session is to appear on. Always a real workspace id — neither door mints one.</summary>
    public string WorkspaceId { get; }

    /// <summary>Which rule produced <see cref="WorkspaceId"/>, carried through to the audit trail.</summary>
    public SpawnCaller Caller { get; }

    /// <summary>The verified pane of a host-derived caller, or null for the assistant — which has no pane, and whose target is therefore not derived from one.</summary>
    public string? CallerPaneId { get; }

    /// <summary>
    /// The assistant's rule: the workspace was <em>named</em> by the caller, because the assistant has no desk of
    /// its own to infer one from. Reachable only behind the assistant's own pane check and the visual consent gate —
    /// naming a target is exactly what makes confirming it necessary.
    /// </summary>
    public static SpawnTarget NamedByTheAssistant(string workspaceId) =>
        new(workspaceId, SpawnCaller.Assistant, callerPaneId: null);

    /// <summary>
    /// A coordinator's rule (AC-436): the workspace was derived host-side from the caller's own verified pane, and
    /// no parameter contributed to it. The seam is written down now, unused, so the next implementer starts from
    /// "derive it" rather than from "validate what was passed" — see the remarks on this type for why that
    /// distinction is the guardrail.
    /// </summary>
    /// <param name="workspaceId">The desk <paramref name="callerPaneId"/> was found on — never a value the caller supplied.</param>
    /// <param name="callerPaneId">The transport-verified pane of the agent asking, as <c>McpRequestContext.CurrentPaneId</c> reports it.</param>
    public static SpawnTarget DerivedFromTheCallersPane(string workspaceId, string callerPaneId) =>
        new(workspaceId, SpawnCaller.Coordinator, callerPaneId);
}

/// <summary>Who a spawn was made by, in the audit trail's words. One value per scoping rule in <see cref="SpawnTarget"/>.</summary>
public enum SpawnCaller
{
    /// <summary>The cockpit's own voice assistant, which named its target.</summary>
    Assistant,

    /// <summary>An in-workspace coordinator agent (AC-436), whose target was derived from its pane.</summary>
    Coordinator,
}
