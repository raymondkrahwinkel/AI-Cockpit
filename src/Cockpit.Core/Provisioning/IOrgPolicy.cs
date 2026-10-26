namespace Cockpit.Core.Provisioning;

/// <summary>
/// AC-1593: the organisation's policy (manifest-v1 §3.7) as last applied on this machine, clamped wherever a session uses it.
/// </summary>
public interface IOrgPolicy
{
    /// <summary>The policy with <see cref="ProvisionPolicy.By"/> as shown in "Managed by …"; null when no manifest set one.</summary>
    Task<ProvisionPolicy?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>The same policy for a synchronous reader, re-read after every save of the settings. When they cannot be read it
    /// never throws: it answers with a policy that forbids terminal and shell access, and tries again on the next read.</summary>
    ProvisionPolicy? Current { get; }
}
