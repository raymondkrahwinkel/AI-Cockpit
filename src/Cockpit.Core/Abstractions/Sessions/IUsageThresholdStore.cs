using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>Where the operator's own usage thresholds live (AC-233), on top of what each provider declared.</summary>
public interface IUsageThresholdStore
{
    /// <summary>What has been set; empty settings mean every signal follows its provider's declaration.</summary>
    Task<UsageThresholdSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the whole set.</summary>
    Task SaveAsync(UsageThresholdSettings settings, CancellationToken cancellationToken = default);
}
