using Cockpit.Core.Plugins;

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

        Assert.Equal(PluginLoadDecision.AbstractionsMajorMismatch, PluginLoadPolicy.Decide(Manifest(abstractionsVersion: 2), HostMajor, saved, "abc"));
    }

    // AC-1402: contract 3 is the first the plugin SDK ships without Avalonia. A plugin built for 2 is refused, with the
    // reason the manager shows; the same plugin built for 3 loads.
    [Theory]
    [InlineData(2, PluginLoadDecision.AbstractionsMajorMismatch, "Built for plugin contract version 2, this cockpit provides 3")]
    [InlineData(3, PluginLoadDecision.Load, null)]
    public void APluginBuiltForAnotherContract_IsRefusedWithItsReason_AndOneBuiltForThisContractLoads(
        int builtFor, PluginLoadDecision expectedDecision, string? expectedReason)
    {
        var saved = new PluginRegistration(Enabled: true, PinnedSha256: "abc");

        var decision = PluginLoadPolicy.Decide(Manifest(builtFor), Cockpit.Plugins.Abstractions.AbstractionsContract.Version, saved, "abc");
        var reason = PluginCompatibility.IncompatibilityReason(
            new PluginStoreVersion("1.0.0", "x-1.0.0.zip", builtFor, null, null, null),
            Cockpit.Plugins.Abstractions.AbstractionsContract.Version,
            new Version(0, 46, 0));

        Assert.Equal((expectedDecision, expectedReason), (decision, reason));
    }

    [Fact]
    public void Decide_NeverSeen_NeedsConsent()
    {
        Assert.Equal(PluginLoadDecision.NeedsConsent, PluginLoadPolicy.Decide(Manifest(), HostMajor, saved: null, "abc"));
    }

    [Fact]
    public void Decide_Disabled_IsSkipped()
    {
        var saved = new PluginRegistration(Enabled: false, PinnedSha256: "abc");

        Assert.Equal(PluginLoadDecision.Disabled, PluginLoadPolicy.Decide(Manifest(), HostMajor, saved, "abc"));
    }

    [Fact]
    public void Decide_EnabledButHashChanged_NeedsConsentAgain()
    {
        var saved = new PluginRegistration(Enabled: true, PinnedSha256: "old-hash");

        Assert.Equal(PluginLoadDecision.NeedsConsent, PluginLoadPolicy.Decide(Manifest(), HostMajor, saved, "new-hash"));
    }

    [Fact]
    public void Decide_EnabledAndHashMatches_Loads()
    {
        var saved = new PluginRegistration(Enabled: true, PinnedSha256: "ABC");

        // Hash comparison is case-insensitive (hex).
        Assert.Equal(PluginLoadDecision.Load, PluginLoadPolicy.Decide(Manifest(), HostMajor, saved, "abc"));
    }
}
