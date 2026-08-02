namespace Cockpit.Core.Abstractions.Assistant;

// Which desk a spawn lands on, and — inseparably — *how that was decided* (AC-545).
// *Read this before adding a second caller.* One host-side spawn service serves two callers with two
// different levels of authority, and the difference between them is not a check that runs after the target is
// known — it is *how the target is arrived at in the first place*:
// -
//   <term>the assistant (AC-545)</term>
//   <description>names the workspace outright. It sits on no desk at all
//   (`SessionWorkspacePlacement` places it nowhere, by construction), so there is nothing to derive a target
//   from — and that is precisely why its spawns are confirmed out loud and on screen before anything starts.</description>
// -
//   <term>a coordinator (AC-436, not built yet)</term>
//   <description>does not get to name anything. Its target is derived host-side from the
//   transport-verified `McpRequestContext.CurrentPaneId`, and its tool takes no workspace parameter at all, so
//   a coordinator cannot reach a desk that is not its own however it phrases the request.</description>
//
// *The strict rule is a different rule, not a filter on the permissive one.* The assistant's rule landed
// first, and the cheap way to add the coordinator's later is "the same call, plus a check that the workspace it
// passed is its own". Do not. That shape keeps a parameter on the wire that the coordinator's whole guarantee is
// that it does not have, and it makes the guardrail a validation someone can weaken, relax for one caller, or
// forget on the next tool — instead of an absence the caller cannot argue with. AC-436's resolver must read the
// caller's pane and construct `DerivedFromTheCallersPane` itself; it must never accept a workspace id
// from its caller and hand it to `NamedByTheAssistant`.
//
// *Which is why this type has no public constructor.* The two factories below are the only two doors, they
// are named after the two rules rather than after their arguments, and each one stamps who came through it — so
// the audit trail records the authority a spawn was made under and not merely the desk it landed on. A third
// caller with a third rule adds a third factory here, next to these, where the comparison is unavoidable.
public sealed record SpawnTarget
{
    private SpawnTarget(string workspaceId, SpawnCaller caller, string? callerPaneId)
    {
        WorkspaceId = workspaceId;
        Caller = caller;
        CallerPaneId = callerPaneId;
    }

    // The desk the session is to appear on. Always a real workspace id — neither door mints one.
    public string WorkspaceId { get; }

    // Which rule produced `WorkspaceId`, carried through to the audit trail.
    public SpawnCaller Caller { get; }

    // The verified pane of a host-derived caller, or null for the assistant — which has no pane, and whose target is therefore not derived from one.
    public string? CallerPaneId { get; }

    // The assistant's rule: the workspace was *named* by the caller, because the assistant has no desk of
    // its own to infer one from. Reachable only behind the assistant's own pane check and the visual consent gate —
    // naming a target is exactly what makes confirming it necessary.
    public static SpawnTarget NamedByTheAssistant(string workspaceId) =>
        new(workspaceId, SpawnCaller.Assistant, callerPaneId: null);

    // A coordinator's rule (AC-436): the workspace was derived host-side from the caller's own verified pane, and
    // no parameter contributed to it. The seam is written down now, unused, so the next implementer starts from
    // "derive it" rather than from "validate what was passed" — see the remarks on this type for why that
    // distinction is the guardrail.
    //
    // `workspaceId`: The desk `callerPaneId` was found on — never a value the caller supplied.
    // `callerPaneId`: The transport-verified pane of the agent asking, as `McpRequestContext.CurrentPaneId` reports it.
    public static SpawnTarget DerivedFromTheCallersPane(string workspaceId, string callerPaneId) =>
        new(workspaceId, SpawnCaller.Coordinator, callerPaneId);
}

// Who a spawn was made by, in the audit trail's words. One value per scoping rule in `SpawnTarget`.
public enum SpawnCaller
{
    // The cockpit's own voice assistant, which named its target.
    Assistant,

    // An in-workspace coordinator agent (AC-436), whose target was derived from its pane.
    Coordinator,
}
