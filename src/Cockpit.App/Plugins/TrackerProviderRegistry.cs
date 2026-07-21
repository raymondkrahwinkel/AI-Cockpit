using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the trackers plugins register (<c>ICockpitHost.AddTrackerProvider</c>), so a consumer (Autopilot) can find
/// the one for an issue's tracker id and post back to it. A registry of its own, the same shape as
/// <see cref="IWorkspaceTypeRegistry"/>, so a consumer reads it without depending on the tracker plugins. Empty until
/// a tracker plugin is installed.
/// </summary>
public interface ITrackerProviderRegistry
{
    /// <summary>Records a tracker provider. A tracker id that is already registered is refused, first one wins.</summary>
    /// <returns>False when another plugin already contributes this tracker id — the caller says so; nothing throws.</returns>
    bool Register(ITrackerProvider provider);

    /// <summary>Every tracker registered so far, in registration order.</summary>
    IReadOnlyList<ITrackerProvider> Providers { get; }
}

internal sealed class TrackerProviderRegistry : ITrackerProviderRegistry, ISingletonService
{
    private readonly List<ITrackerProvider> _providers = [];

    public IReadOnlyList<ITrackerProvider> Providers => [.. _providers];

    public bool Register(ITrackerProvider provider)
    {
        if (_providers.Any(existing => string.Equals(existing.TrackerId, provider.TrackerId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _providers.Add(provider);
        return true;
    }
}
