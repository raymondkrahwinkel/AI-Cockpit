namespace Cockpit.Core.Delegation;

/// <summary>
/// A delegation the target profile does not allow (#67). Thrown rather than swallowed so the calling agent is
/// told plainly why — an agent that gets a silent no-op cannot correct course, and a guard that fails quietly is
/// indistinguishable from one that is not there.
/// </summary>
public sealed class DelegationRejectedException(string message) : Exception(message);
