using Cockpit.Core.Abstractions;
using Microsoft.Extensions.Logging;
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
    /// Holds <see cref="Changed"/> back until the returned scope is disposed, then raises it once if anything changed. A
    /// plugin update withdraws and registers its trackers in one go, which a running Autopilot must not read as a departure.
    /// </summary>
    IDisposable HoldChanges();

    /// <summary>
    /// Every tracker registered so far, in registration order.
    /// </summary>
    IReadOnlyList<ITrackerProvider> Providers { get; }
}

internal sealed class TrackerProviderRegistry(ILogger<TrackerProviderRegistry>? logger = null) : ITrackerProviderRegistry, ISingletonService
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

        _Raise();
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

        _Raise();
        return true;
    }

    public IDisposable HoldChanges()
    {
        lock (_providers)
        {
            _holds++;
        }

        return new Hold(this);
    }

    private int _holds;
    private bool _heldChange;

    private void _Raise()
    {
        lock (_providers)
        {
            if (_holds > 0)
            {
                _heldChange = true;
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void _Release()
    {
        bool raise;
        lock (_providers)
        {
            raise = --_holds == 0 && _heldChange;
            _heldChange &= _holds > 0;
        }

        if (raise)
        {
            // The scope ends an update that already happened: a subscriber that throws is logged, not reported as its failure.
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "A tracker-change subscriber threw after a plugin update.");
            }
        }
    }

    private sealed class Hold(TrackerProviderRegistry registry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                registry._Release();
            }
        }
    }
}
