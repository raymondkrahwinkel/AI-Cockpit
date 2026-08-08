using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Infrastructure.Voice;
using Cockpit.Plugins.Abstractions.Sessions;
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

        Assert.True(checker.IsLoggedIn(local), "a local provider has no login gate to fail");
    }

    [Fact]
    public void Login_PluginProfile_DispatchesToTheProvidersGate()
    {
        var checker = new ProfileLoginChecker(RegistryWith(Registration("claude", isLoggedIn: _ => false)));
        var profile = new SessionProfile("p", new PluginProviderConfig("claude", "{}"));

        Assert.False(checker.IsLoggedIn(profile), "the provider's gate reported logged out");
    }

    [Fact]
    public void Login_PluginProfileWhoseProviderDeclaresNoGate_IsTreatedAsReady()
    {
        var checker = new ProfileLoginChecker(RegistryWith(Registration("codex", isLoggedIn: null)));
        var profile = new SessionProfile("p", new PluginProviderConfig("codex", "{}"));

        Assert.True(checker.IsLoggedIn(profile), "a provider with no gate manages its own auth");
    }

    private static IPluginProviderRegistry SessionRegistryWith(string providerId, Func<string, bool>? isLoggedIn)
    {
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve(providerId).Returns(new SessionProviderRegistration(
            providerId,
            providerId,
            _ => Substitute.For<IPluginSessionDriverFactory>(),
            new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false, SupportsVision: false),
            _ => Substitute.For<IPluginProviderConfigView>())
        {
            IsLoggedIn = isLoggedIn,
        });

        return registry;
    }

    // AC-629: Gemini, GitHub Models and Kimi register only a session provider. Before the fallback the checker
    // resolved nothing for them and every profile read as ready — a logged-out one included.
    [Fact]
    public void Login_SdkOnlyProvider_DispatchesToItsSessionRegistration()
    {
        var checker = new ProfileLoginChecker(RegistryWith(), SessionRegistryWith("gemini", _ => false));
        var profile = new SessionProfile("p", new PluginProviderConfig("gemini", "{}"));

        Assert.False(checker.IsLoggedIn(profile), "the SDK-only provider's own gate reported logged out");
    }

    [Fact]
    public void Login_SdkOnlyProviderDeclaringNoGate_IsStillTreatedAsReady()
    {
        var checker = new ProfileLoginChecker(RegistryWith(), SessionRegistryWith("gemini", isLoggedIn: null));
        var profile = new SessionProfile("p", new PluginProviderConfig("gemini", "{}"));

        Assert.True(checker.IsLoggedIn(profile), "growing the contract must not invent a gate nobody declared");
    }

    // A provider filling both routes declares the pair once; the TTY side keeps first say so either route gives
    // the same answer rather than two gates disagreeing.
    [Fact]
    public void Login_ProviderOnBothRoutes_UsesTheTtyGate()
    {
        var checker = new ProfileLoginChecker(
            RegistryWith(Registration("claude", isLoggedIn: _ => false)),
            SessionRegistryWith("claude", _ => true));
        var profile = new SessionProfile("p", new PluginProviderConfig("claude", "{}"));

        Assert.False(checker.IsLoggedIn(profile));
    }

    // The host resolves the checker from the container, where the session registry is always present — but the
    // parameter is optional so the existing construction keeps compiling. That path must not throw.
    [Fact]
    public void Login_WithNoSessionRegistryAtAll_FallsBackToReady()
    {
        var checker = new ProfileLoginChecker(RegistryWith());
        var profile = new SessionProfile("p", new PluginProviderConfig("gemini", "{}"));

        Assert.True(checker.IsLoggedIn(profile));
    }

    [Fact]
    public void Transcript_ProviderWithNoReader_SnapshotsEmpty()
    {
        var reader = new SessionTranscriptReader(
            Substitute.For<IServiceProvider>(),
            RegistryWith(Registration("codex", createTranscriptReader: null)));
        var profile = new SessionProfile("p", new PluginProviderConfig("codex", "{}"));

        Assert.Empty(reader.SnapshotTranscripts(profile));
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

        Assert.Contains("existing.jsonl", reader.SnapshotTranscripts(profile));
    }
}
