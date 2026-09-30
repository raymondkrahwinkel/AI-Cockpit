using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The pure load decision for a discovered plugin (#14): version gate, then consent/enabled/hash state.</summary>
public class PluginLoadPolicyTests
{
    private const int HostMajor = 1;

    private static PluginManifest Manifest(int abstractionsVersion = HostMajor) =>
        new("x", "X", "1.0.0", "X.dll", abstractionsVersion, null, null, null, null);

    // Version gate first, then consent/enabled/hash. `enabled` null is a plugin that was never seen; the hash compares
    // case-insensitively (hex).
    [Theory]
    [InlineData(2, true, "abc", "abc", PluginLoadDecision.AbstractionsMajorMismatch)]
    [InlineData(HostMajor, null, null, "abc", PluginLoadDecision.NeedsConsent)]
    [InlineData(HostMajor, false, "abc", "abc", PluginLoadDecision.Disabled)]
    [InlineData(HostMajor, true, "old-hash", "new-hash", PluginLoadDecision.NeedsConsent)]
    [InlineData(HostMajor, true, "ABC", "abc", PluginLoadDecision.Load)]
    public void Decide_FollowsTheVersionGate_ThenTheConsentEnabledAndHashState(
        int builtFor, bool? enabled, string? pinned, string current, PluginLoadDecision expected)
    {
        PluginRegistration? saved = enabled is null ? null : new PluginRegistration(Enabled: enabled.Value, PinnedSha256: pinned ?? string.Empty);

        Assert.Equal(expected, PluginLoadPolicy.Decide(Manifest(builtFor), HostMajor, saved, current));
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
}
