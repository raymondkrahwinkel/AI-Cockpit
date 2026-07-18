namespace Cockpit.Plugin.Kubernetes.Security;

/// <summary>
/// What <see cref="ClusterAccessGate"/> decided about one requested action: allowed, or denied with a reason the
/// MCP tool can hand back to the agent. A denial is either the operator saying no to a consent prompt, or a policy
/// block (a capability that is off for the cluster) — the reason distinguishes them so the agent is told what to
/// do about it (ask the operator, or turn the capability on in settings).
/// </summary>
internal sealed record GateResult(bool IsAllowed, string? DeniedReason)
{
    public static GateResult Allow { get; } = new(true, null);

    public static GateResult Deny(string reason) => new(false, reason);
}
