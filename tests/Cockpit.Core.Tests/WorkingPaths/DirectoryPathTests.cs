using Cockpit.Core.WorkingPaths;
using FluentAssertions;

namespace Cockpit.Core.Tests.WorkingPaths;

/// <summary>
/// Comparing folders the way the platform does — the rule everything that decides what a session works on shares,
/// so a folder means one thing across the app rather than one thing per caller.
/// </summary>
public class DirectoryPathTests
{
    [Fact]
    public void Normalize_TrailingSeparatorsAndRelativeSegments_AreOneFolder() =>
        DirectoryPath.Normalize("/repos/cockpit/src/../").Should().Be(DirectoryPath.Normalize("/repos/cockpit"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0invalid")]
    public void Normalize_WhatNamesNoFolder_IsNull(string? path) =>
        // A path the platform itself rejects answers null rather than throwing: this runs on the way to starting a
        // session, and an unusable path is an answer, not a failure.
        DirectoryPath.Normalize(path).Should().BeNull();

    [Fact]
    public void IsWithin_TheFolderItself_Counts() =>
        DirectoryPath.IsWithin("/repos/cockpit", "/repos/cockpit").Should().BeTrue();

    [Fact]
    public void IsWithin_SomethingInside_Counts() =>
        DirectoryPath.IsWithin("/repos/cockpit/src/Core", "/repos/cockpit").Should().BeTrue();

    [Fact]
    public void IsWithin_ASiblingSharingAPrefix_DoesNot() =>
        DirectoryPath.IsWithin("/repos/cockpit-plugins", "/repos/cockpit").Should().BeFalse();

    [Fact]
    public void IsWithin_ARootFolder_ContainsEverything() =>
        // A root keeps its separator through Normalize — a root without one is not a path — so the containment test
        // must not insist on a second one, or a project scoped to a drive would claim nothing at all.
        DirectoryPath.IsWithin(Path.GetFullPath("/home/foo"), Path.GetFullPath("/")).Should().BeTrue();

    [Fact]
    public void IsWithin_TheOtherWayRound_DoesNot() =>
        DirectoryPath.IsWithin("/repos", "/repos/cockpit").Should().BeFalse();

    [Theory]
    [InlineData(null, "/repos/cockpit")]
    [InlineData("/repos/cockpit", null)]
    [InlineData("", "")]
    public void IsWithin_WithoutTwoRealFolders_IsNever(string? path, string? folder) =>
        DirectoryPath.IsWithin(path, folder).Should().BeFalse();
}
