using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// <see cref="TtySessionProviderResolver"/>: which TUI (if any) a profile runs (#45 fase B2). Now that Claude is a
/// provider plugin (Fase 4), a Claude profile is a <c>PluginProviderConfig("claude", …)</c> and a profile-less
/// session resolves the bundled Claude plugin — both wrap the plugin's own <see cref="IPluginTtyProvider"/> in a
/// <see cref="PluginTtySessionProviderAdapter"/>. A local HTTP provider (Ollama/LM Studio) and a plugin that
/// registered no TTY provider both resolve to null, which the New-session dialog and the TTY panel take for an
/// answer rather than launching something the operator never chose.
/// </summary>
public class TtySessionProviderResolverTests
{
    private static TtySessionProviderResolver _CreateResolver(IPluginTtyProviderRegistry? ttyProviderRegistry = null)
    {
        var serviceProvider = new ServiceCollection().AddSingleton(new McpAuthKey()).BuildServiceProvider();
        return new TtySessionProviderResolver(serviceProvider, ttyProviderRegistry ?? Substitute.For<IPluginTtyProviderRegistry>());
    }

    private static IPluginTtyProviderRegistry _RegistryWith(string providerId)
    {
        var registry = Substitute.For<IPluginTtyProviderRegistry>();
        var registration = new TtyProviderRegistration(providerId, "Claude", _ => Substitute.For<IPluginTtyProvider>(), Options: []);
        registry.Resolve(providerId).Returns(registration);
        return registry;
    }

    [Fact]
    public void Resolve_AClaudeProfile_ResolvesTheBundledClaudePluginsTtyProvider()
    {
        var resolver = _CreateResolver(_RegistryWith(ClaudePluginProfile.ProviderId));
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/config/work", null));

        var resolved = resolver.Resolve(profile);

        resolved.Should().BeOfType<PluginTtySessionProviderAdapter>();
        resolved!.ProviderId.Should().Be(ClaudePluginProfile.ProviderId);
    }

    [Fact]
    public void Resolve_ANullProfile_ResolvesTheBundledClaudePlugin_SinceAProfileLessSessionRunsTheHostsOwnCli()
    {
        var resolver = _CreateResolver(_RegistryWith(ClaudePluginProfile.ProviderId));

        var resolved = resolver.Resolve(null);

        resolved.Should().BeOfType<PluginTtySessionProviderAdapter>();
        resolved!.ProviderId.Should().Be(ClaudePluginProfile.ProviderId);
    }

    [Fact]
    public void Resolve_AnOllamaProfile_ReturnsNull_SinceALocalHttpModelIsNotAProgramATerminalCanHost()
    {
        var resolver = _CreateResolver();
        var profile = new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1"));

        resolver.Resolve(profile).Should().BeNull();
    }

    [Fact]
    public void Resolve_ALmStudioProfile_ReturnsNull()
    {
        var resolver = _CreateResolver();
        var profile = new SessionProfile("local", new LmStudioConfig("http://localhost:1234", "some-model"));

        resolver.Resolve(profile).Should().BeNull();
    }

    [Fact]
    public void Resolve_APluginProfileWithATtyRegistration_ReturnsAPluginTtySessionProviderAdapter_UnderTheSameProviderId()
    {
        var registry = Substitute.For<IPluginTtyProviderRegistry>();
        var innerProvider = Substitute.For<IPluginTtyProvider>();
        var registration = new TtyProviderRegistration(
            "cli-agent-provider.codex", "Codex (CLI)", _ => innerProvider, Options: []);
        registry.Resolve("cli-agent-provider.codex").Returns(registration);
        var resolver = _CreateResolver(registry);
        var profile = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", """{"Command":"codex"}"""));

        var resolved = resolver.Resolve(profile);

        resolved.Should().BeOfType<PluginTtySessionProviderAdapter>();
        resolved!.ProviderId.Should().Be("cli-agent-provider.codex");
    }

    [Fact]
    public void Resolve_APluginProfileWithNoTtyRegistration_ReturnsNull_SinceThatProviderOffersNoTui()
    {
        var registry = Substitute.For<IPluginTtyProviderRegistry>();
        registry.Resolve("gemini-provider.gemini").Returns((TtyProviderRegistration?)null);
        var resolver = _CreateResolver(registry);
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", "{}"));

        resolver.Resolve(profile).Should().BeNull();
    }
}
