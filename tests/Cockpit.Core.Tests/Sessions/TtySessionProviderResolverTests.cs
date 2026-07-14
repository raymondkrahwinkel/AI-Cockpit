using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Configuration;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// <see cref="TtySessionProviderResolver"/>: which TUI (if any) a profile runs (#45 fase B2). Claude and a
/// profile-less session always resolve to the host's own <see cref="ClaudeTtySessionProvider"/>; a plugin
/// profile resolves to its own <see cref="IPluginTtyProvider"/> wrapped in a <see cref="PluginTtySessionProviderAdapter"/>
/// only when it registered one under the same provider id its session provider uses — a local HTTP provider
/// (Ollama/LM Studio) and a plugin that registered no TTY provider both resolve to null, which the New-
/// session dialog and <c>ClaudeTtyViewModel</c> both take for an answer rather than launching something the
/// operator never chose.
/// </summary>
public class TtySessionProviderResolverTests
{
    private static ClaudeTtySessionProvider _CreateClaudeProvider()
    {
        var emptyMcpStore = Substitute.For<IMcpServerStore>();
        emptyMcpStore.LoadAsync().Returns(Task.FromResult<IReadOnlyList<McpServerConfig>>([]));

        return new ClaudeTtySessionProvider(
            Options.Create(new CockpitOptions()),
            Substitute.For<IClaudeExecutableLocator>(),
            new WorkspaceTrustWriter(),
            emptyMcpStore);
    }

    private static TtySessionProviderResolver _CreateResolver(
        out ClaudeTtySessionProvider claudeProvider,
        IPluginTtyProviderRegistry? ttyProviderRegistry = null)
    {
        claudeProvider = _CreateClaudeProvider();
        var services = new ServiceCollection();
        services.AddSingleton(claudeProvider);
        var serviceProvider = services.BuildServiceProvider();

        return new TtySessionProviderResolver(serviceProvider, ttyProviderRegistry ?? Substitute.For<IPluginTtyProviderRegistry>());
    }

    [Fact]
    public void Resolve_AClaudeProfile_ReturnsTheClaudeTtySessionProvider()
    {
        var resolver = _CreateResolver(out var claudeProvider);
        var profile = new SessionProfile("work", new ClaudeConfig("/config/work"));

        resolver.Resolve(profile).Should().BeSameAs(claudeProvider);
    }

    [Fact]
    public void Resolve_ANullProfile_ReturnsTheClaudeTtySessionProvider_SinceAProfileLessSessionRunsTheHostsOwnCli()
    {
        var resolver = _CreateResolver(out var claudeProvider);

        resolver.Resolve(null).Should().BeSameAs(claudeProvider);
    }

    [Fact]
    public void Resolve_AnOllamaProfile_ReturnsNull_SinceALocalHttpModelIsNotAProgramATerminalCanHost()
    {
        var resolver = _CreateResolver(out _);
        var profile = new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1"));

        resolver.Resolve(profile).Should().BeNull();
    }

    [Fact]
    public void Resolve_ALmStudioProfile_ReturnsNull()
    {
        var resolver = _CreateResolver(out _);
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
        var resolver = _CreateResolver(out _, registry);
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
        var resolver = _CreateResolver(out _, registry);
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", "{}"));

        resolver.Resolve(profile).Should().BeNull();
    }
}
