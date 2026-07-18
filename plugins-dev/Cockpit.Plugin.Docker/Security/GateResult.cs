namespace Cockpit.Plugin.Docker.Security;

/// <summary>The outcome of a consent gate check: allowed, or denied with a reason to hand back to the agent.</summary>
internal sealed record GateResult(bool IsAllowed, string? DeniedReason)
{
    public static GateResult Allow { get; } = new(true, null);

    public static GateResult Deny(string reason) => new(false, reason);
}
