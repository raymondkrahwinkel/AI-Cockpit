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
    // Reading every pre-release as a nightly would put a build on a feed it was never packed for. The tag gate
    // turns away anything that is not vX.Y.Z, so a release candidate came from somewhere else and stable offers
    // it less rather than more; a version that says nothing at all gets the same answer for the same reason.
    [InlineData("1.0.0-rc.1", UpdateChannel.Stable)]
    [InlineData("", UpdateChannel.Stable)]
    public void ABuild_FollowsTheStreamItsOwnVersionNames(string version, UpdateChannel expected) =>
        Assert.Equal(expected, BuildChannel.FromVersion(version));

    /// <summary>
    /// The rolling tag is republished every night, so a nightly install cannot ask "is this a different release
    /// than mine" — it asks whether the version climbed. Two runs of the same night's tag differ, which is what
    /// makes the answer possible at all; and the release beats the nightlies leading up to it, so a nightly
    /// install crossing onto stable moves forward.
    /// </summary>
    [Theory]
    [InlineData("0.8.0-nightly.6", "0.8.0-nightly.5")]
    [InlineData("0.8.0", "0.8.0-nightly.99")]
    public void TheVersionsTheWorkflowsPack_SortTheWayTheUpdaterNeeds(string later, string earlier) =>
        Assert.True(SemanticVersion.Parse(later) > SemanticVersion.Parse(earlier));
}
