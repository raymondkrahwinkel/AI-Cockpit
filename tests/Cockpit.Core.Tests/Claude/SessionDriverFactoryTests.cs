using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="SessionDriverFactory"/>'s plugin-provider arm (#45): a profile carrying a
/// <see cref="PluginProviderConfig"/> resolves the registered provider from <see cref="IPluginProviderRegistry"/>,
/// mints its driver through <see cref="SessionProviderRegistration.CreateDriverFactory"/>, and hands back a
/// <see cref="PluginSessionDriverAdapter"/> wrapping it — the built-in Ollama/LM-Studio/Claude-CLI arms are
/// unchanged and out of scope here.
/// </summary>
public class SessionDriverFactoryTests
{
    [Fact]
    public void Create_WithAPluginProfile_ResolvesTheRegisteredProviderAndReturnsAnAdapterAroundItsDriver()
    {
        var innerDriver = new FakePluginSessionDriver();
        var driverFactory = Substitute.For<IPluginSessionDriverFactory>();
        driverFactory.Create("""{"apiKey":"secret"}""").Returns(innerDriver);
        var registration = new SessionProviderRegistration(
            ProviderId: "gemini-provider.gemini",
            DisplayName: "Gemini",
            CreateDriverFactory: _ => driverFactory,
            Capabilities: new PluginSessionCapabilities(true, false),
            CreateConfigView: _ => Substitute.For<IPluginProviderConfigView>());

        var registry = new PluginProviderRegistry();
        registry.Register(registration);
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new SessionDriverFactory(services, registry);
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", """{"apiKey":"secret"}"""));

        var driver = factory.Create(profile);

        driver.Should().BeOfType<PluginSessionDriverAdapter>();
        driver.Capabilities.SupportsTools.Should().BeTrue();
        driverFactory.Received(1).Create("""{"apiKey":"secret"}""");
    }

    [Fact]
    public void Create_WithAPluginProfile_WhenNoProviderIsRegisteredUnderThatId_Throws()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new SessionDriverFactory(services, new PluginProviderRegistry());
        var profile = new SessionProfile("gemini", new PluginProviderConfig("unknown-provider", "{}"));

        var act = () => factory.Create(profile);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown-provider*");
    }

    [Fact]
    public void Create_WithAPluginProvider_ButNoPluginProviderConfigOnTheProfile_Throws()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new SessionDriverFactory(services, new PluginProviderRegistry());
        // Constructing a profile whose Provider reports Plugin without a matching config record should not
        // normally happen (ProviderConfig.Provider always agrees), but the factory must still fail loudly
        // rather than silently misbehave if it ever does — proven via a minimal ProviderConfig subclass.
        var profile = new SessionProfile("broken", new _MismatchedProviderConfig());

        var act = () => factory.Create(profile);

        act.Should().Throw<InvalidOperationException>().WithMessage("*PluginProviderConfig*");
    }

    private sealed record _MismatchedProviderConfig() : ProviderConfig(SessionProvider.Plugin);
}
