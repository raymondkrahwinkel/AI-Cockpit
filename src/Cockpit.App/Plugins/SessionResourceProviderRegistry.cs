using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the session-resource providers plugins register (<c>ICockpitHost.AddSessionResourceProvider</c>, AC-165),
/// so the launch routes can ask them without depending on the plugins that contribute them. A registry of its own,
/// the same shape as <see cref="IProjectFieldRegistry"/>. Empty until a plugin that gives sessions something is
/// installed, which is the ordinary state.
/// </summary>
public interface ISessionResourceProviderRegistry
{
    /// <summary>Records a provider. The same instance registered twice is refused, so a plugin re-initialised in place is not asked twice.</summary>
    /// <returns>False when this provider is already registered — the caller says so; nothing throws.</returns>
    bool Register(ISessionResourceProvider provider);

    /// <summary>Every provider registered so far, in registration order — the order their contributions claim names in.</summary>
    IReadOnlyList<ISessionResourceProvider> Providers { get; }
}

internal sealed class SessionResourceProviderRegistry : ISessionResourceProviderRegistry, ISingletonService
{
    private readonly List<ISessionResourceProvider> _providers = [];

    public IReadOnlyList<ISessionResourceProvider> Providers => [.. _providers];

    // Reference equality, not a key: a provider has no id to collide on, and two plugins that both give a session
    // something is the point rather than a clash. What this refuses is the same object twice — a plugin whose
    // Initialize ran again would otherwise have its contribution counted twice on every launch.
    public bool Register(ISessionResourceProvider provider)
    {
        if (_providers.Contains(provider))
        {
            return false;
        }

        _providers.Add(provider);
        return true;
    }
}
