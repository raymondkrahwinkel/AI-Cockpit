using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a profile's <see cref="DelegationPolicy"/> (#67) in the <c>profiles</c> section. Absent for a
/// profile that is not a delegation target, which is every profile until it is opted in by hand.
/// </summary>
internal sealed class DelegationPolicyEntry
{
    public bool AllowedAsTarget { get; set; }

    public int MaxConcurrent { get; set; } = 1;

    public List<string>? AllowedWorkingDirs { get; set; }

    public string PermissionCeiling { get; set; } = DelegationPolicy.DefaultPermissionCeiling;

    public bool MayDelegateFurther { get; set; }

    public int TimeoutMinutes { get; set; } = DelegationPolicy.DefaultTimeoutMinutes;

    public List<string>? AllowedTaskTypes { get; set; }

    public string? Purpose { get; set; }

    public List<string>? Tags { get; set; }

    public static DelegationPolicyEntry? FromDomain(DelegationPolicy? policy) => policy is null
        ? null
        : new DelegationPolicyEntry
        {
            AllowedAsTarget = policy.AllowedAsTarget,
            MaxConcurrent = policy.MaxConcurrent,
            AllowedWorkingDirs = policy.AllowedWorkingDirs?.ToList(),
            PermissionCeiling = policy.PermissionCeiling,
            MayDelegateFurther = policy.MayDelegateFurther,
            TimeoutMinutes = policy.TimeoutMinutes,
            AllowedTaskTypes = policy.AllowedTaskTypes?.ToList(),
            Purpose = policy.Purpose,
            Tags = policy.Tags?.ToList(),
        };

    public DelegationPolicy ToDomain() => new(
        AllowedAsTarget,
        MaxConcurrent,
        AllowedWorkingDirs,
        PermissionCeiling,
        MayDelegateFurther,
        TimeoutMinutes,
        AllowedTaskTypes,
        Purpose,
        Tags);
}
