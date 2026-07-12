using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// <see cref="IPluginProviderRegistry"/> backed by a plain dictionary guarded by a lock — registrations only
/// ever happen during plugin phase-2 <c>Initialize</c> (a handful of calls at startup), and lookups happen
/// per session-start, so a simple lock beats a lock-free structure's complexity here. A singleton (#45): one
/// registry shared by every <c>CockpitHost</c> (registration) and <see cref="SessionDriverFactory"/> (resolution).
/// </summary>
internal sealed class PluginProviderRegistry : IPluginProviderRegistry, ISingletonService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionProviderRegistration> _byProviderId = [];
    private readonly List<SessionProviderRegistration> _ordered = [];

    public void Register(SessionProviderRegistration registration)
    {
        lock (_gate)
        {
            if (_byProviderId.TryGetValue(registration.ProviderId, out var existing))
            {
                _ordered.Remove(existing);
            }

            _byProviderId[registration.ProviderId] = registration;
            _ordered.Add(registration);
        }
    }

    public IReadOnlyList<SessionProviderRegistration> Registrations
    {
        get
        {
            lock (_gate)
            {
                return [.. _ordered];
            }
        }
    }

    public SessionProviderRegistration? Resolve(string providerId)
    {
        lock (_gate)
        {
            return _byProviderId.GetValueOrDefault(providerId);
        }
    }
}
