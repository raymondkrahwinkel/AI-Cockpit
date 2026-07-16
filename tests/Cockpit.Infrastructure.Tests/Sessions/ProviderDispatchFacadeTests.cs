using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Infrastructure.Voice;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// The generic host-side façades that replaced the in-tree Claude machinery (Fase 4): both the login gate and
/// the transcript reader dispatch a profile to its provider plugin through the TTY registry, so the core carries
/// no provider-specific behaviour. These cover the dispatch branches that a bug would turn into a blocked login
/// or a silently-dead read-aloud.
/// </summary>
public class ProviderDispatchFacadeTests
{
    private static TtyProviderRegistration Registration(
        string providerId,
        Func<string, bool>? isLoggedIn = null,
        Func<IServiceProvider, IPluginTranscriptReader>? createTranscriptReader = null) =>
        new(providerId, providerId, _ => Substitute.For<IPluginTtyProvider>(), [])
        {
            IsLoggedIn = isLoggedIn,
            CreateTranscriptReader = createTranscriptReader,
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

    [Fact]
    public void Login_NonPluginProfile_IsAlwaysReady()
    {
        var checker = new ProfileLoginChecker(RegistryWith());
        var local = new SessionProfile("local", new OllamaConfig("http://localhost", "llama"));

        checker.IsLoggedIn(local).Should().BeTrue("a local provider has no login gate to fail");
    }

    [Fact]
    public void Login_PluginProfile_DispatchesToTheProvidersGate()
    {
        var checker = new ProfileLoginChecker(RegistryWith(Registration("claude", isLoggedIn: _ => false)));
        var profile = new SessionProfile("p", new PluginProviderConfig("claude", "{}"));

        checker.IsLoggedIn(profile).Should().BeFalse("the provider's gate reported logged out");
    }

    [Fact]
    public void Login_PluginProfileWhoseProviderDeclaresNoGate_IsTreatedAsReady()
    {
        var checker = new ProfileLoginChecker(RegistryWith(Registration("codex", isLoggedIn: null)));
        var profile = new SessionProfile("p", new PluginProviderConfig("codex", "{}"));

        checker.IsLoggedIn(profile).Should().BeTrue("a provider with no gate manages its own auth");
    }

    [Fact]
    public void Transcript_ProviderWithNoReader_SnapshotsEmpty()
    {
        var reader = new SessionTranscriptReader(
            Substitute.For<IServiceProvider>(),
            RegistryWith(Registration("codex", createTranscriptReader: null)));
        var profile = new SessionProfile("p", new PluginProviderConfig("codex", "{}"));

        reader.SnapshotTranscripts(profile).Should().BeEmpty("the provider records no tailable transcript");
    }

    [Fact]
    public void Transcript_PluginProfile_DispatchesToTheProvidersReader()
    {
        var inner = Substitute.For<IPluginTranscriptReader>();
        inner.SnapshotTranscripts("{}").Returns(new HashSet<string> { "existing.jsonl" });
        var reader = new SessionTranscriptReader(
            Substitute.For<IServiceProvider>(),
            RegistryWith(Registration("claude", createTranscriptReader: _ => inner)));
        var profile = new SessionProfile("p", new PluginProviderConfig("claude", "{}"));

        reader.SnapshotTranscripts(profile).Should().Contain("existing.jsonl");
    }
}
