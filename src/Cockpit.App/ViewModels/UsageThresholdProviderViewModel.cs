namespace Cockpit.App.ViewModels;

// One provider's group of usage-threshold rows on the settings screen (AC-233).
public sealed class UsageThresholdProviderViewModel(string providerId, string displayName, IReadOnlyList<UsageThresholdRowViewModel> signals)
{
    // The provider these thresholds belong to.
    public string ProviderId { get; } = providerId;

    // What the operator reads in the group's header.
    public string DisplayName { get; } = displayName;

    // Every signal this provider declared, in the order it declared them.
    public IReadOnlyList<UsageThresholdRowViewModel> Signals { get; } = signals;
}
