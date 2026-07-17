using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
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
        var services = new ServiceCollection().AddSingleton(new McpAuthKey()).BuildServiceProvider();
        var factory = new SessionDriverFactory(services, registry);
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", """{"apiKey":"secret"}"""));

        var driver = factory.Create(profile);

        driver.Should().BeOfType<PluginSessionDriverAdapter>();
        driver.Capabilities.SupportsTools.Should().BeTrue();
        driverFactory.Received(1).Create("""{"apiKey":"secret"}""");
    }

    [Fact]
    public async Task Create_WithAPluginProfile_WiresTheMcpServerCatalog_SoTheSessionsSelectionFansOutToTheDriver()
    {
        var innerDriver = new FakePluginSessionDriver();
        var driverFactory = Substitute.For<IPluginSessionDriverFactory>();
        driverFactory.Create(Arg.Any<string>()).Returns(innerDriver);
        var registration = new SessionProviderRegistration(
            ProviderId: "cli-agent-provider.codex",
            DisplayName: "Codex",
            CreateDriverFactory: _ => driverFactory,
            Capabilities: new PluginSessionCapabilities(true, true),
            CreateConfigView: _ => Substitute.For<IPluginProviderConfigView>());
        var registry = new PluginProviderRegistry();
        registry.Register(registration);

        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://127.0.0.1:8765/mcp" },
        });
        var services = new ServiceCollection().AddSingleton(new McpAuthKey()).AddSingleton(catalog).BuildServiceProvider();
        var factory = new SessionDriverFactory(services, registry);
        var profile = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"));

        var driver = factory.Create(profile);
        await driver.StartAsync(enabledMcpServerNames: new HashSet<string> { "cockpit-orchestrator" });

        // The factory must hand the effective MCP catalog (registry + plugin-provided servers, AC-11) to the
        // adapter, or the operator's per-session MCP selection never reaches the plugin driver — the
        // "Connected (0 tools)" regression.
        innerDriver.LastMcpServers.Should().ContainSingle().Which.Name.Should().Be("cockpit-orchestrator");
    }

    [Fact]
    public void Create_WithAProfilelessSession_RunsTheBundledClaudeProviderPlugin()
    {
        // Fase 4: Claude is a provider plugin like every other, so a profile-less default session runs the bundled
        // Claude plugin (there is no in-tree driver to fall back to) with an empty default config.
        var innerDriver = new FakePluginSessionDriver();
        var driverFactory = Substitute.For<IPluginSessionDriverFactory>();
        driverFactory.Create(Arg.Any<string>()).Returns(innerDriver);
        var registration = new SessionProviderRegistration(
            ProviderId: "claude",
            DisplayName: "Claude",
            CreateDriverFactory: _ => driverFactory,
            Capabilities: new PluginSessionCapabilities(true, true),
            CreateConfigView: _ => Substitute.For<IPluginProviderConfigView>());
        var registry = new PluginProviderRegistry();
        registry.Register(registration);
        var services = new ServiceCollection().AddSingleton(new McpAuthKey()).BuildServiceProvider();
        var factory = new SessionDriverFactory(services, registry);

        var driver = factory.Create(profile: null);

        driver.Should().BeOfType<PluginSessionDriverAdapter>();
        // A profile-less default session runs the bundled Claude plugin with an empty default config.
        driverFactory.Received(1).Create("{}");
    }

    [Fact]
    public void Create_WithAPluginProfile_WhenNoProviderIsRegisteredUnderThatId_Throws()
    {
        var services = new ServiceCollection().AddSingleton(new McpAuthKey()).BuildServiceProvider();
        var factory = new SessionDriverFactory(services, new PluginProviderRegistry());
        var profile = new SessionProfile("gemini", new PluginProviderConfig("unknown-provider", "{}"));

        var act = () => factory.Create(profile);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown-provider*");
    }

    [Fact]
    public void Create_WithAPluginProvider_ButNoPluginProviderConfigOnTheProfile_Throws()
    {
        var services = new ServiceCollection().AddSingleton(new McpAuthKey()).BuildServiceProvider();
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
