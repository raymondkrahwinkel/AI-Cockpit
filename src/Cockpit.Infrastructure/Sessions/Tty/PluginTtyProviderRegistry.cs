using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Collects the <see cref="TtyProviderRegistration"/>s plugins register via <c>ICockpitHost.AddTtyProvider</c>
/// and resolves one by provider id. The TTY counterpart of <see cref="IPluginProviderRegistry"/>, and a separate
/// registry for the same reason the registration is separate: a provider offers a headless driver, a TUI, or
/// both, and which of the two it has is not a property of the other.
/// </summary>
public interface IPluginTtyProviderRegistry
{
    /// <summary>Registers <paramref name="registration"/>; a later registration under the same provider id replaces the earlier one.</summary>
    void Register(TtyProviderRegistration registration);

    /// <summary>Every TTY provider registered so far, in registration order.</summary>
    IReadOnlyList<TtyProviderRegistration> Registrations { get; }

    /// <summary>The registration for <paramref name="providerId"/>, or <see langword="null"/> when that provider offers no TUI.</summary>
    TtyProviderRegistration? Resolve(string providerId);
}

internal sealed class PluginTtyProviderRegistry : IPluginTtyProviderRegistry, ISingletonService
{
    private readonly Dictionary<string, TtyProviderRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TtyProviderRegistration> Registrations => [.. _registrations.Values];

    public void Register(TtyProviderRegistration registration) =>
        _registrations[registration.ProviderId] = registration;

    public TtyProviderRegistration? Resolve(string providerId) =>
        _registrations.GetValueOrDefault(providerId);
}
