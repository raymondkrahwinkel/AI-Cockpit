using Cockpit.Core.Updates;
using Velopack;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// Which stream a build follows when nobody has chosen one (AC-387), and that the versions the workflows pack order
/// the way the updater needs them to.
/// <para>
/// The ordering half replaces <c>UpdateComparisonTests</c>. The cockpit compared releases itself while a nightly had
/// no version to compare — it was a rolling tag and nothing else — so identity fell to the commit. Since <c>vpk pack</c>
/// stamps <c>-nightly.&lt;run&gt;</c> a nightly has a version that climbs, Velopack does the comparing, and what is
/// left worth guarding is that our own version shapes sort as intended by the thing now doing the sorting.
/// </para>
/// </summary>
public class BuildChannelTests
{
    [Theory]
    [InlineData("0.8.0-nightly.123", UpdateChannel.Nightly)]
    [InlineData("0.8.0-nightly.123+abc1234", UpdateChannel.Nightly)]
    [InlineData("0.8.0", UpdateChannel.Stable)]
    [InlineData("0.8.0+abc1234", UpdateChannel.Stable)]
    public void ABuild_FollowsTheStreamItsOwnVersionNames(string version, UpdateChannel expected) =>
        Assert.Equal(expected, BuildChannel.FromVersion(version));

    /// <summary>
    /// Reading every pre-release as a nightly would put a build on a feed it was never packed for. A release
    /// candidate is not even something the pipeline can produce — the release workflow's tag gate turns away
    /// anything that is not <c>vX.Y.Z</c> — so this is a build from somewhere else, and stable is the answer that
    /// offers it less rather than more.
    /// </summary>
    [Fact]
    public void AReleaseCandidate_IsNotANightly() =>
        Assert.Equal(UpdateChannel.Stable, BuildChannel.FromVersion("1.0.0-rc.1"));

    /// <summary>
    /// A build that cannot say what it is gets the channel that offers less. The opposite default is the whole
    /// failure this exists to remove, only pointing the other way.
    /// </summary>
    [Fact]
    public void AVersionThatSaysNothing_IsStable() =>
        Assert.Equal(UpdateChannel.Stable, BuildChannel.FromVersion(string.Empty));

    /// <summary>
    /// The rolling tag is republished every night, so a nightly install cannot ask "is this a different release than
    /// mine" — it asks whether the version climbed. Two runs of the same night's tag differ here, which is what makes
    /// the answer possible at all.
    /// </summary>
    [Fact]
    public void OneNightlyToTheNext_Climbs() =>
        Assert.True(SemanticVersion.Parse("0.8.0-nightly.6") > SemanticVersion.Parse("0.8.0-nightly.5"));

    /// <summary>The release beats the nightlies leading up to it, so a nightly install crossing onto stable moves forward.</summary>
    [Fact]
    public void TheRelease_BeatsTheNightliesOfItself() =>
        Assert.True(SemanticVersion.Parse("0.8.0") > SemanticVersion.Parse("0.8.0-nightly.99"));
}
