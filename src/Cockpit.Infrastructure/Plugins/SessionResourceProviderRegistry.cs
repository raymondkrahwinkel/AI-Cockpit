using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Holds the session-resource providers plugins register (<c>ICockpitHost.AddSessionResourceProvider</c>, AC-165),
/// so the launch routes can ask them without depending on the contributing plugins. Same shape as
/// <see cref="IProjectFieldRegistry"/>.
/// </summary>
public interface ISessionResourceProviderRegistry
{
    /// <summary>
    /// Records a provider. The same instance registered twice is refused, so a plugin re-initialised in place is not asked twice.
    /// </summary>
    /// <returns>
    /// False when this provider is already registered — the caller says so; nothing throws.
    /// </returns>
    bool Register(ISessionResourceProvider provider);

    /// <summary>
    /// Withdraws <paramref name="provider"/>; false when it is not registered.
    /// </summary>
    bool Unregister(ISessionResourceProvider provider);

    event EventHandler? Changed;

    /// <summary>
    /// Every provider registered so far, in registration order — the order their contributions claim names in.
    /// </summary>
    IReadOnlyList<ISessionResourceProvider> Providers { get; }
}

internal sealed class SessionResourceProviderRegistry : ISessionResourceProviderRegistry, ISingletonService
{
    private readonly List<ISessionResourceProvider> _providers = [];

    public event EventHandler? Changed;

    public IReadOnlyList<ISessionResourceProvider> Providers
    {
        get
        {
            lock (_providers)
            {
                return [.. _providers];
            }
        }
    }

    // Reference equality, not a key: a provider has no id to collide on, and two plugins that both give a session
    // something is the point rather than a clash. What this refuses is the same object twice — a plugin whose
    // Initialize ran again would otherwise have its contribution counted twice on every launch.
    public bool Register(ISessionResourceProvider provider)
    {
        lock (_providers)
        {
            if (_providers.Contains(provider))
            {
                return false;
            }

            _providers.Add(provider);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Unregister(ISessionResourceProvider provider)
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
