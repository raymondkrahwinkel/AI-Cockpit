using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// `IPluginProviderRegistry` backed by a dictionary guarded by a lock — registrations happen only during
// plugin phase-2 `Initialize` (few calls at startup) and lookups per session-start, so a simple lock beats
// a lock-free structure here. Singleton (#45): shared by `CockpitHost` (register) and `SessionDriverFactory` (resolve).
internal sealed class PluginProviderRegistry : IPluginProviderRegistry, ISingletonService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionProviderRegistration> _byProviderId = [];
    private readonly List<SessionProviderRegistration> _ordered = [];
    private readonly Dictionary<string, string?> _owners = [];

    public void Register(SessionProviderRegistration registration, string? ownerPluginId = null)
    {
        lock (_gate)
        {
            _owners[registration.ProviderId] = ownerPluginId;
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

    public string? OwnerOf(string providerId)
    {
        lock (_gate)
        {
            return _owners.GetValueOrDefault(providerId);
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
