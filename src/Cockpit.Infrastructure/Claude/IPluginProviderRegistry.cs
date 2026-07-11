using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// Collects <see cref="SessionProviderRegistration"/>s a plugin registers via <c>ICockpitHost.AddSessionProvider</c>
/// (#45) and resolves one by its provider id — the "closed world" of built-in providers (#26) replaced by an
/// open registry for plugin-backed ones. Public (not internal) so <c>Cockpit.App.Plugins.CockpitHost</c> and
/// the profile-editor view models can resolve it from the container without an <c>InternalsVisibleTo</c> grant,
/// mirroring the existing <c>PluginBootstrap</c> precedent for a cross-project-visible Infrastructure type.
/// </summary>
public interface IPluginProviderRegistry
{
    /// <summary>Registers <paramref name="registration"/>; a later registration with the same <see cref="SessionProviderRegistration.ProviderId"/> replaces the earlier one.</summary>
    void Register(SessionProviderRegistration registration);

    /// <summary>Every provider registered so far, in registration order.</summary>
    IReadOnlyList<SessionProviderRegistration> Registrations { get; }

    /// <summary>The registration for <paramref name="providerId"/>, or <see langword="null"/> when nothing is registered under that id.</summary>
    SessionProviderRegistration? Resolve(string providerId);
}
