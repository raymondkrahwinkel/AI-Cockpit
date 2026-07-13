using Cockpit.Core.Updates;
using FluentAssertions;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// Whether a release is worth telling the operator about (#71). The failure that matters is not missing an update —
/// it is announcing one that is not there. A notification that cries wolf is one people learn to dismiss, and then
/// the real update goes unseen too.
/// </summary>
public class UpdateComparisonTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.2", true)]
    [InlineData("1.3.0", "1.2.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.2", "1.2.3", false)]
    public void ATaggedRelease_IsComparedByItsVersion(string theirs, string ours, bool newer) =>
        UpdateComparison.IsNewer(_Release(theirs), ours, "abc1234").Should().Be(newer);

    [Fact]
    public void TheTagsLeadingV_IsNotPartOfTheVersion() =>
        UpdateComparison.IsNewer(_Release("v1.2.3"), "1.2.2", string.Empty).Should().BeTrue();

    [Fact]
    public void OurOwnBuildMetadata_DoesNotMakeUsLookOlder() =>
        UpdateComparison.IsNewer(_Release("1.2.3"), "1.2.3+ac50049", string.Empty).Should().BeFalse();

    [Fact]
    public void AReleasedVersion_BeatsThePreReleaseOfItself() =>
        UpdateComparison.IsNewer(_Release("1.2.0"), "1.2.0-nightly.4", string.Empty).Should().BeTrue();

    [Fact]
    public void AVersionNobodyCanRead_IsNotAnUpdate() =>
        UpdateComparison.IsNewer(_Release("tuesday"), "1.2.3", string.Empty).Should().BeFalse();

    [Fact]
    public void ANightly_IsComparedByItsCommit_BecauseARollingTagHasNoVersion()
    {
        // The nightly tag is overwritten every night, so "is it newer" cannot be asked of its version — it has none.
        // The only honest question is whether it was built from a different commit than we were.
        var nightly = _Nightly("9f8e7d6");

        UpdateComparison.IsNewer(nightly, "0.1.0-nightly.4", "ac50049").Should().BeTrue();
        UpdateComparison.IsNewer(nightly, "0.1.0-nightly.4", "9f8e7d6").Should().BeFalse();
    }

    [Fact]
    public void AShortShaAgainstALongOne_IsTheSameCommit() =>
        // The release carries the full sha; the build's informational version carries the short one, or the other way
        // round depending on who wrote it. Announcing an update for the commit you are already running is the most
        // embarrassing possible false positive.
        UpdateComparison.IsNewer(_Nightly("ac500497f3b21c9d0e4a"), "0.1.0", "ac50049").Should().BeFalse();

    [Fact]
    public void ANightlyWithNoCommitToCompare_IsNotAnnounced() =>
        UpdateComparison.IsNewer(_Nightly(string.Empty), "0.1.0", "ac50049").Should().BeFalse();

    [Fact]
    public void ABuildThatDoesNotKnowItsOwnCommit_IsNotToldAboutNightlies() =>
        UpdateComparison.IsNewer(_Nightly("9f8e7d6"), "0.1.0", string.Empty).Should().BeFalse();

    private static AppRelease _Release(string version) =>
        new(version, "abc1234", $"Cockpit {version}", "notes", "https://example/releases/1", DateTimeOffset.UtcNow, IsPrerelease: false);

    private static AppRelease _Nightly(string commit) =>
        new(string.Empty, commit, "Nightly", "notes", "https://example/releases/nightly", DateTimeOffset.UtcNow, IsPrerelease: true);
}
