using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="PluginProviderRegistry"/> (#45): registers/resolves plugin-provided session providers by their
/// provider id, and re-registering the same id replaces the earlier registration rather than duplicating it.
/// </summary>
public class PluginProviderRegistryTests
{
    [Fact]
    public void Register_ThenResolve_ReturnsTheSameRegistration()
    {
        var registry = new PluginProviderRegistry();
        var registration = _Registration("gemini-provider.gemini");

        registry.Register(registration);

        Assert.Equal(registration, registry.Resolve("gemini-provider.gemini"));
    }

    [Fact]
    public void Resolve_WhenNothingIsRegisteredUnderThatId_ReturnsNull()
    {
        var registry = new PluginProviderRegistry();

        Assert.Null(registry.Resolve("unknown"));
    }

    [Fact]
    public void Registrations_ListsEveryRegisteredProvider_InRegistrationOrder()
    {
        var registry = new PluginProviderRegistry();
        var first = _Registration("gemini-provider.gemini");
        var second = _Registration("gemini-provider.openai");

        registry.Register(first);
        registry.Register(second);

        Assert.Equal(new[] { first, second }, registry.Registrations);
    }

    [Fact]
    public void Register_WithAnAlreadyRegisteredProviderId_ReplacesTheEarlierRegistration()
    {
        var registry = new PluginProviderRegistry();
        var original = _Registration("gemini-provider.gemini", "Gemini");
        var replacement = _Registration("gemini-provider.gemini", "Gemini (updated)");

        registry.Register(original);
        registry.Register(replacement);

        Assert.Equal(replacement, registry.Resolve("gemini-provider.gemini"));
        Assert.Single(registry.Registrations);
    }

    private static SessionProviderRegistration _Registration(string providerId, string displayName = "Test Provider") => new(
        ProviderId: providerId,
        DisplayName: displayName,
        CreateDriverFactory: _ => Substitute.For<IPluginSessionDriverFactory>(),
        Capabilities: new PluginSessionCapabilities(false, false),
        CreateConfigView: _ => Substitute.For<IPluginProviderConfigView>());
}
