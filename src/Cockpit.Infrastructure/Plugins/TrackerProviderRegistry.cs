using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Holds the trackers plugins register (<c>ICockpitHost.AddTrackerProvider</c>), so a consumer (Autopilot) finds the
/// one for an issue's tracker id and posts back to it. Same shape as <see cref="IWorkspaceTypeRegistry"/>, read
/// without depending on the tracker plugins.
/// </summary>
public interface ITrackerProviderRegistry
{
    /// <summary>
    /// Records a tracker provider. A tracker id that is already registered is refused, first one wins.
    /// </summary>
    /// <returns>
    /// False when another plugin already contributes this tracker id — the caller says so; nothing throws.
    /// </returns>
    bool Register(ITrackerProvider provider);

    /// <summary>
    /// Withdraws <paramref name="provider"/>; false when it is not registered.
    /// </summary>
    bool Unregister(ITrackerProvider provider);

    event EventHandler? Changed;

    /// <summary>
    /// Every tracker registered so far, in registration order.
    /// </summary>
    IReadOnlyList<ITrackerProvider> Providers { get; }
}

internal sealed class TrackerProviderRegistry : ITrackerProviderRegistry, ISingletonService
{
    private readonly List<ITrackerProvider> _providers = [];

    public event EventHandler? Changed;

    public IReadOnlyList<ITrackerProvider> Providers
    {
        get
        {
            lock (_providers)
            {
                return [.. _providers];
            }
        }
    }

    public bool Register(ITrackerProvider provider)
    {
        lock (_providers)
        {
            if (_providers.Any(existing => string.Equals(existing.TrackerId, provider.TrackerId, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _providers.Add(provider);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Unregister(ITrackerProvider provider)
    {
        lock (_providers)
        {
            if (!_providers.Remove(provider))
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
