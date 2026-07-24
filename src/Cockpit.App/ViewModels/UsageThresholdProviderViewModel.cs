namespace Cockpit.App.ViewModels;

/// <summary>One provider's group of usage-threshold rows on the settings screen (AC-233).</summary>
public sealed class UsageThresholdProviderViewModel(string providerId, string displayName, IReadOnlyList<UsageThresholdRowViewModel> signals)
{
    /// <summary>The provider these thresholds belong to.</summary>
    public string ProviderId { get; } = providerId;

    /// <summary>What the operator reads in the group's header.</summary>
    public string DisplayName { get; } = displayName;

    /// <summary>Every signal this provider declared, in the order it declared them.</summary>
    public IReadOnlyList<UsageThresholdRowViewModel> Signals { get; } = signals;
}
