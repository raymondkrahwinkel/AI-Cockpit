namespace Cockpit.Plugin.Docker.Security;

// The outcome of a consent gate check: allowed, or denied with a reason to hand back to the agent. BypassNote
// (AC-1348) is set when the consent mode pre-approved this call — the tool result surfaces it so the skip is
// visible in the transcript, not just the audit log.
internal sealed record GateResult(bool IsAllowed, string? DeniedReason, string? BypassNote = null)
{
    public static GateResult Allow { get; } = new(true, null);

    public static GateResult Deny(string reason) => new(false, reason);
}
