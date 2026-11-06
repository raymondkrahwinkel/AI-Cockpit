using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Collects the <see cref="TtyProviderRegistration"/>s plugins register via <c>ICockpitHost.AddTtyProvider</c>
/// and resolves one by provider id. The TTY counterpart of <see cref="IPluginProviderRegistry"/>, separate for
/// the same reason the registration is: a provider offers a headless driver, a TUI, or both — independently.
/// </summary>
public interface IPluginTtyProviderRegistry
{
    /// <summary>
    /// Registers <paramref name="registration"/>; a later registration under the same provider id replaces the earlier one.
    /// </summary>
    void Register(TtyProviderRegistration registration, string? ownerPluginId = null);

    /// <summary>
    /// Withdraws <paramref name="providerId"/> while <paramref name="ownerPluginId"/> still owns it; a later owner's registration stays.
    /// </summary>
    bool Unregister(string providerId, string? ownerPluginId);

    /// <summary>
    /// The plugin the host stamped as registering <paramref name="providerId"/> (AC-289), or <see langword="null"/> when unknown.
    /// </summary>
    string? OwnerOf(string providerId);

    /// <summary>
    /// Every TTY provider registered so far, in registration order.
    /// </summary>
    IReadOnlyList<TtyProviderRegistration> Registrations { get; }

    /// <summary>
    /// The registration for <paramref name="providerId"/>, or <see langword="null"/> when that provider offers no TUI.
    /// </summary>
    TtyProviderRegistration? Resolve(string providerId);
}

internal sealed class PluginTtyProviderRegistry : IPluginTtyProviderRegistry, ISingletonService
{
    private readonly Dictionary<string, TtyProviderRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TtyProviderRegistration> Registrations
    {
        get
        {
            lock (_registrations)
            {
                return [.. _registrations.Values];
            }
        }
    }

    private readonly Dictionary<string, string?> _owners = new(StringComparer.OrdinalIgnoreCase);

    public void Register(TtyProviderRegistration registration, string? ownerPluginId = null)
    {
        lock (_registrations)
        {
            _registrations[registration.ProviderId] = registration;
            _owners[registration.ProviderId] = ownerPluginId;
        }
    }

    public bool Unregister(string providerId, string? ownerPluginId)
    {
        lock (_registrations)
        {
            return _registrations.ContainsKey(providerId)
                && string.Equals(_owners.GetValueOrDefault(providerId), ownerPluginId, StringComparison.Ordinal)
                && _registrations.Remove(providerId)
                && _owners.Remove(providerId);
        }
    }

    public string? OwnerOf(string providerId)
    {
        lock (_registrations)
        {
            return _owners.GetValueOrDefault(providerId);
        }
    }

    public TtyProviderRegistration? Resolve(string providerId)
    {
        lock (_registrations)
        {
            return _registrations.GetValueOrDefault(providerId);
        }
    }
}
