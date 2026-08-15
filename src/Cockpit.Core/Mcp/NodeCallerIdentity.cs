namespace Cockpit.Core.Mcp;

// Who a caller that reached this cockpit over the network node listener is (AC-791), and the whole authorization
// model for one — the question AC-790 left open when it opened that listener.
//
// The model, in four answers:
//
// *Which identity.* A remote caller is not a session and never gets one: `SessionMcpKeyring` maps a bearer to the
// pane it was minted for, and every pane it can name is a window on this machine. A controller on another machine
// has no pane, so it stands beside the keyring rather than in it — authenticated by the node's persistent shared
// secret, and stamped onto `McpRequestContext` as this one reserved id. It is deliberately *an* identity rather
// than the null the middleware stamped before: null is what the in-process tool loop carries, so leaving a network
// caller on null put a machine off this desk in the same bucket as the cockpit's own tool loop — one bucket the
// consent broker keys remembered approvals on (`ConsentService.RequestConsentAsync`). A remembered "yes" given to
// a local call would have covered a remote one silently. Now they are two identities and neither inherits the
// other's answers.
//
// *One per node, not one per controller.* The epic's role split (AC-742, and the pairing handshake in AC-792) fixes
// a node to exactly one controller at a time — a controller carries many nodes, never the other way round. So
// "the remote caller" and "my controller" are the same party, and one credential naming one identity says
// everything a per-controller list would. A second controller is not a second identity to authorize; it is a
// pairing this node refuses.
//
// *Lifetime.* The identity itself is a constant — it is the *role*, not a credential. What expires is the shared
// secret behind it (`NodeEndpointSettings.SharedSecret`), which lives until it is rotated or the master switch goes
// off, and is stored encrypted at rest like every other secret-named field. There is no session to end and nothing
// to revoke on a timer: a remote caller is authorized for exactly as long as the operator leaves the node open.
//
// *What revoking means.* Turning the master switch off, or rotating the secret, ends the one coupling there is —
// there are no sibling grants to spare, which is why revocation needs no bookkeeping here. AC-792's unpair hangs
// on this: it is the same act, reached from the pairing screen instead of the Security tab.
//
// What this identity may reach is decided one level up, in `CockpitMcpEndpointHost`: an `Internal` endpoint
// (AC-204 — the assistant's read and act tools) binds no network listener at all, so it is not refused off-machine
// but absent. Beyond that, every tool that keys on the transport-verified pane already fails closed for this id
// without a line of new code, because it resolves to no session and no workspace: `set_status` matches no pane,
// `list_agents`/`notify`/`set_wake_optin` refuse a caller the cockpit cannot place on a desk, and the assistant's
// own tools compare against `AssistantIdentity.PaneId` and do not match. `read_inbox` is the one that answers
// rather than refuses — it hands over the mail addressed to this id, and nothing addresses it. The delegation
// tools would scope to this id's own tasks (none, where a null caller sees every task on the machine), though that
// is defence in depth rather than a hole closed: `OrchestratorMcpServer` binds its own loopback-only listener and
// is not one of the endpoints a node exposes. Narrowing further — which profiles and projects a node may run — is
// AC-794's job, not this constant's.
//
// One consequence worth stating because it is a posture and not an accident: a remote caller cannot be *asked*
// anything. A consent prompt is routed to the pane that raised it and denied when there is nowhere to show it
// (`CockpitViewModel._OnConsentPromptOpened`), and this id is a pane that can never exist — so any tool call from
// a controller that needs the operator's approval is refused rather than put on some local session's screen under
// a name that would not say where it came from. Until AC-794 gives an operator something to grant, refusing is the
// only answer this cockpit can honestly give on their behalf.
public static class NodeCallerIdentity
{
    // The pane id every request authenticated by the node's shared secret is stamped with. Shaped like
    // `AssistantIdentity.PaneId` and for the same reason: it is checked against `McpRequestContext.CurrentPaneId`,
    // which is stamped host-side from the transport, so it is not a secret and no argument on any tool can move it.
    // A real pane id is a generated identifier, so this literal cannot collide with one.
    public const string PaneId = "cockpit-node-controller";
}
