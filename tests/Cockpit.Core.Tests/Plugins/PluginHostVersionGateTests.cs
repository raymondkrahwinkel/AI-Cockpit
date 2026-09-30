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

    // Rows, in order: too old is refused; new enough loads; a declared 1.0.0 (the template default) does not bite a 0.x
    // host, while an honest sub-1.0 requirement (AC-181) is enforced against one; a manifest with nothing usable in
    // the field is not refused over it (a typo must not become an outage); the contract major still wins over all.
    [Theory]
    [InlineData("2.0.0", 1, "1.5.0", PluginLoadDecision.HostTooOld)]
    [InlineData("1.0.0", 1, "1.5.0", PluginLoadDecision.Load)]
    [InlineData("1.0.0", 1, "0.1.0", PluginLoadDecision.Load)]
    [InlineData("0.14.0", 1, "0.13.0", PluginLoadDecision.HostTooOld)]
    [InlineData("0.10.0", 1, "0.13.0", PluginLoadDecision.Load)]
    [InlineData(null, 1, "1.5.0", PluginLoadDecision.Load)]
    [InlineData("", 1, "1.5.0", PluginLoadDecision.Load)]
    [InlineData("not-a-version", 1, "1.5.0", PluginLoadDecision.Load)]
    [InlineData("9.0.0", 2, "1.5.0", PluginLoadDecision.AbstractionsMajorMismatch)]
    public void TheMinHostVersionGate_RefusesWhatTheHostIsTooOldFor_AndNothingElse(
        string? minHostVersion, int hostAbstractionsMajor, string hostVersion, PluginLoadDecision expected)
    {
        var decision = PluginLoadPolicy.Decide(
            Manifest(minHostVersion), hostAbstractionsMajor, Consented("abc"), currentSha256: "abc",
            hostVersion: Version.Parse(hostVersion));

        Assert.Equal(expected, decision);
    }
}
