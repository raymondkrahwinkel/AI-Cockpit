using FluentAssertions;
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

        decision.Should().Be(PluginLoadDecision.HostTooOld);
    }

    [Fact]
    public void APluginTheHostIsNewEnoughFor_Loads()
    {
        var decision = PluginLoadPolicy.Decide(
            Manifest("1.0.0"), hostAbstractionsMajor: 1, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(1, 5, 0));

        decision.Should().Be(PluginLoadDecision.Load);
    }

    [Fact]
    public void BeforeTheCockpitReachesOnePointZero_TheGateDoesNotBite()
    {
        // Every manifest in existence claims minHostVersion 1.0.0 — the template's default — while the host is
        // 0.1.0. Enforcing the gate against a 0.x host would refuse every plugin including the bundled ones, over
        // a number nobody meant. This is the test that keeps that from happening by accident.
        var decision = PluginLoadPolicy.Decide(
            Manifest("1.0.0"), hostAbstractionsMajor: 1, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(0, 1, 0));

        decision.Should().Be(PluginLoadDecision.Load);
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

        decision.Should().Be(PluginLoadDecision.Load);
    }

    [Fact]
    public void TheContractMajor_StillWinsOverEverything()
    {
        var decision = PluginLoadPolicy.Decide(
            Manifest("9.0.0"), hostAbstractionsMajor: 2, Consented("abc"), currentSha256: "abc",
            hostVersion: new Version(1, 5, 0));

        decision.Should().Be(PluginLoadDecision.AbstractionsMajorMismatch);
    }
}
