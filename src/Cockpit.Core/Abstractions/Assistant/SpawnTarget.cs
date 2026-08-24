using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Assistant;

// AC-1013: Which desk a spawn lands on, inseparably from how that was decided (AC-545) — the assistant names its
// target (no desk of its own, confirmed on screen), a coordinator's target is derived host-side from its own
// verified pane, no workspace parameter on the wire. No public constructor; only the two named factories below.
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

    // AC-1013: A coordinator's rule (AC-436), unused seam written down now so the next implementer starts from
    // "derive it" rather than "validate what was passed" — workspaceId/callerPaneId are never caller-supplied.
    public static SpawnTarget DerivedFromTheCallersPane(string workspaceId, string callerPaneId) =>
        new(workspaceId, SpawnCaller.Coordinator, callerPaneId);

    // AC-1013: The paired controller's rule (AC-795) — desk is the node's own active workspace, derived here,
    // never named by the remote caller, which cannot see this node's desks. Consent stands in via [e]'s
    // AC-794 grant (revocable live) rather than a per-spawn click on a machine nobody is at.
    public static SpawnTarget RequestedByThePairedController(string workspaceId) =>
        new(workspaceId, SpawnCaller.Controller, NodeCallerIdentity.PaneId);
}

// Who a spawn was made by, in the audit trail's words. One value per scoping rule in `SpawnTarget`.
public enum SpawnCaller
{
    // The cockpit's own voice assistant, which named its target.
    Assistant,

    // An in-workspace coordinator agent (AC-436), whose target was derived from its pane.
    Coordinator,

    // The cockpit on the other machine this one is paired to as a node (AC-795), whose target was derived from
    // this machine's own active desk.
    Controller,
}
