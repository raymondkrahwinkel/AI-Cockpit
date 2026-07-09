using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The pure load decision for a discovered plugin (#14): version gate, then consent/enabled/hash state.</summary>
public class PluginLoadPolicyTests
{
    private const int HostMajor = 1;

    private static PluginManifest Manifest(int abstractionsVersion = HostMajor) =>
        new("x", "X", "1.0.0", "X.dll", abstractionsVersion, null, null, null, null);

    [Fact]
    public void Decide_AbstractionsMajorMismatch_IsRefused_EvenWhenEnabledAndMatchingHash()
    {
        var saved = new PluginRegistration(Enabled: true, PinnedSha256: "abc");

        PluginLoadPolicy.Decide(Manifest(abstractionsVersion: 2), HostMajor, saved, "abc")
            .Should().Be(PluginLoadDecision.AbstractionsMajorMismatch);
    }

    [Fact]
    public void Decide_NeverSeen_NeedsConsent()
    {
        PluginLoadPolicy.Decide(Manifest(), HostMajor, saved: null, "abc")
            .Should().Be(PluginLoadDecision.NeedsConsent);
    }

    [Fact]
    public void Decide_Disabled_IsSkipped()
    {
        var saved = new PluginRegistration(Enabled: false, PinnedSha256: "abc");

        PluginLoadPolicy.Decide(Manifest(), HostMajor, saved, "abc")
            .Should().Be(PluginLoadDecision.Disabled);
    }

    [Fact]
    public void Decide_EnabledButHashChanged_NeedsConsentAgain()
    {
        var saved = new PluginRegistration(Enabled: true, PinnedSha256: "old-hash");

        PluginLoadPolicy.Decide(Manifest(), HostMajor, saved, "new-hash")
            .Should().Be(PluginLoadDecision.NeedsConsent);
    }

    [Fact]
    public void Decide_EnabledAndHashMatches_Loads()
    {
        var saved = new PluginRegistration(Enabled: true, PinnedSha256: "ABC");

        // Hash comparison is case-insensitive (hex).
        PluginLoadPolicy.Decide(Manifest(), HostMajor, saved, "abc")
            .Should().Be(PluginLoadDecision.Load);
    }
}
