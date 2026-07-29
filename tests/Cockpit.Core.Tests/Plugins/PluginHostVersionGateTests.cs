using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The <c>minHostVersion</c> gate. It existed as a field in every manifest and was compared by nothing, which
/// meant a plugin could claim whatever it liked — and every one of them claimed 1.0.0 while the host was 0.1.0.
/// <para>
/// It is the only thing that catches a plugin calling a member this host does not have yet: the contract major
/// says nothing about it (the member exists in the SDK it compiled against), so the plugin loads and then fails
/// somewhere the operator cannot see.
/// </para>
/// </summary>
public class PluginHostVersionGateTests
{
    private static PluginManifest Manifest(string? minHostVersion) =>
        new("plug", "Plug", "1.0.0", "Plug.dll", AbstractionsVersion: 1, EntryType: null, minHostVersion,
            Description: null, Author: null);

    private static PluginRegistration Consented(string hash) => new(Enabled: true, PinnedSha256: hash);

    [Fact]
    public void APluginThatNeedsANewerCockpit_IsRefused_NotLoadedAndBroken()
    {
        var decision = PluginLoadPolicy.Decide(
            Manifest("2.0.0"), hostAbstractionsMajor: 1, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(1, 5, 0));

        Assert.Equal(PluginLoadDecision.HostTooOld, decision);
    }

    [Fact]
    public void APluginTheHostIsNewEnoughFor_Loads()
    {
        var decision = PluginLoadPolicy.Decide(
            Manifest("1.0.0"), hostAbstractionsMajor: 1, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(1, 5, 0));

        Assert.Equal(PluginLoadDecision.Load, decision);
    }

    [Fact]
    public void BeforeTheCockpitReachesOnePointZero_ADeclaredOnePointZeroRequirement_DoesNotBite()
    {
        // A manifest claiming minHostVersion 1.0.0 — the plugin template's default — while the host is 0.1.0 is
        // the leftover artifact every manifest used to carry regardless of what it actually needed, not a real
        // requirement. Enforcing it against a 0.x host would refuse every plugin including the bundled ones,
        // over a number nobody meant.
        var decision = PluginLoadPolicy.Decide(
            Manifest("1.0.0"), hostAbstractionsMajor: 1, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(0, 1, 0));

        Assert.Equal(PluginLoadDecision.Load, decision);
    }

    // AC-181: manifests no longer carry the stale 1.0.0 template default — 21+ ship honest sub-1.0 values, each
    // tied to a specific SDK member the host added at that version. That is a real, current requirement, and the
    // gate must enforce it against a 0.x host exactly as it would against a 1.x one — this pins the fix for the
    // bug the AC-181 review found: the pre-1.0 exemption used to suppress this too.
    [Fact]
    public void AnHonestSubOnePointZeroRequirement_IsEnforced_EvenOnASubOnePointZeroHost()
    {
        var decision = PluginLoadPolicy.Decide(
            Manifest("0.14.0"), hostAbstractionsMajor: 1, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(0, 13, 0));

        Assert.Equal(PluginLoadDecision.HostTooOld, decision);
    }

    [Fact]
    public void AnHonestSubOnePointZeroRequirement_TheHostMeets_Loads()
    {
        var decision = PluginLoadPolicy.Decide(
            Manifest("0.10.0"), hostAbstractionsMajor: 1, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(0, 13, 0));

        Assert.Equal(PluginLoadDecision.Load, decision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void AManifestThatSaysNothingUsable_IsNotRefusedOverIt(string? minHostVersion)
    {
        // The field is optional, and a manifest we cannot read a version out of is not a reason to refuse a plugin
        // the operator installed and consented to — that would turn a typo into an outage.
        var decision = PluginLoadPolicy.Decide(
            Manifest(minHostVersion), hostAbstractionsMajor: 1, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(1, 5, 0));

        Assert.Equal(PluginLoadDecision.Load, decision);
    }

    [Fact]
    public void TheContractMajor_StillWinsOverEverything()
    {
        var decision = PluginLoadPolicy.Decide(
            Manifest("9.0.0"), hostAbstractionsMajor: 2, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(1, 5, 0));

        Assert.Equal(PluginLoadDecision.AbstractionsMajorMismatch, decision);
    }
}
