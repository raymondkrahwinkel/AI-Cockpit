namespace Cockpit.Core.Provisioning;

// AC-1593: a change the organisation's policy does not allow, refused with the reason the operator sees.
public sealed class OrgPolicyRefusedException(string message) : InvalidOperationException(message);
