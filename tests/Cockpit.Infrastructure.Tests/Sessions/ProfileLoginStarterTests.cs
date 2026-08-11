using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// <see cref="ProfileLoginStarter"/> — <see cref="ProfileLoginChecker"/>'s sibling (AC-713): the same TTY-then-
/// session dispatch, but for <c>StartLogin</c> instead of <c>IsLoggedIn</c>. Mirrors
/// <see cref="ProviderDispatchFacadeTests"/>'s coverage of the login gate, one level up.
/// </summary>
public class ProfileLoginStarterTests
{
    private static TtyProviderRegistration Registration(string providerId, Func<string, CancellationToken, ILoginFlow>? startLogin) =>
        new(providerId, providerId, _ => Substitute.For<IPluginTtyProvider>(), [])
        {
            StartLogin = startLogin,
        };

    private static IPluginTtyProviderRegistry RegistryWith(params TtyProviderRegistration[] registrations)
    {
        var registry = Substitute.For<IPluginTtyProviderRegistry>();
        foreach (var registration in registrations)
        {
            registry.Resolve(registration.ProviderId).Returns(registration);
        }

        return registry;
    }

    private static IPluginProviderRegistry SessionRegistryWith(string providerId, Func<string, CancellationToken, ILoginFlow>? startLogin)
    {
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve(providerId).Returns(new SessionProviderRegistration(
            providerId,
            providerId,
            _ => Substitute.For<IPluginSessionDriverFactory>(),
            new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false, SupportsVision: false),
            _ => Substitute.For<IPluginProviderConfigView>())
        {
            StartLogin = startLogin,
        });

        return registry;
    }

    [Fact]
    public void NonPluginProfile_HasNothingToDispatchTo()
    {
        var starter = new ProfileLoginStarter(RegistryWith());
        var local = new SessionProfile("local", new OllamaConfig("http://localhost", "llama"));

        Assert.Null(starter.StartLogin(local, CancellationToken.None));
    }

    [Fact]
    public void PluginProfile_DispatchesToTheProvidersStartLogin()
    {
        var flow = Substitute.For<ILoginFlow>();
        var starter = new ProfileLoginStarter(RegistryWith(Registration("claude", (_, _) => flow)));
        var profile = new SessionProfile("p", new PluginProviderConfig("claude", "{}"));

        Assert.Same(flow, starter.StartLogin(profile, CancellationToken.None));
    }

    [Fact]
    public void PluginProfileWhoseProviderDeclaresNoStartLogin_HasNothingToDispatchTo()
    {
        var starter = new ProfileLoginStarter(RegistryWith(Registration("codex", startLogin: null)));
        var profile = new SessionProfile("p", new PluginProviderConfig("codex", "{}"));

        Assert.Null(starter.StartLogin(profile, CancellationToken.None));
    }

    // AC-629's rule for IsLoggedIn applies here too: a provider registering only a session provider must still
    // be reachable through the session registry, not just the TTY one.
    [Fact]
    public void SdkOnlyProvider_DispatchesToItsSessionRegistration()
    {
        var flow = Substitute.For<ILoginFlow>();
        var starter = new ProfileLoginStarter(RegistryWith(), SessionRegistryWith("gemini", (_, _) => flow));
        var profile = new SessionProfile("p", new PluginProviderConfig("gemini", "{}"));

        Assert.Same(flow, starter.StartLogin(profile, CancellationToken.None));
    }

    // A provider filling both routes declares the pair once; the TTY side keeps first say, same as the checker.
    [Fact]
    public void ProviderOnBothRoutes_UsesTheTtyStartLogin()
    {
        var ttyFlow = Substitute.For<ILoginFlow>();
        var sdkFlow = Substitute.For<ILoginFlow>();
        var starter = new ProfileLoginStarter(
            RegistryWith(Registration("claude", (_, _) => ttyFlow)),
            SessionRegistryWith("claude", (_, _) => sdkFlow));
        var profile = new SessionProfile("p", new PluginProviderConfig("claude", "{}"));

        Assert.Same(ttyFlow, starter.StartLogin(profile, CancellationToken.None));
    }

    [Fact]
    public void WithNoSessionRegistryAtAll_DoesNotThrow()
    {
        var starter = new ProfileLoginStarter(RegistryWith());
        var profile = new SessionProfile("p", new PluginProviderConfig("gemini", "{}"));

        Assert.Null(starter.StartLogin(profile, CancellationToken.None));
    }
}
