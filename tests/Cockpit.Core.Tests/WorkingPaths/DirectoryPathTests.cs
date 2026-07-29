using Cockpit.Core.WorkingPaths;

namespace Cockpit.Core.Tests.WorkingPaths;

/// <summary>
/// Comparing folders the way the platform does — the rule everything that decides what a session works on shares,
/// so a folder means one thing across the app rather than one thing per caller.
/// </summary>
public class DirectoryPathTests
{
    [Fact]
    public void Normalize_TrailingSeparatorsAndRelativeSegments_AreOneFolder() =>
        Assert.Equal(DirectoryPath.Normalize("/repos/cockpit"), DirectoryPath.Normalize("/repos/cockpit/src/../"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0invalid")]
    public void Normalize_WhatNamesNoFolder_IsNull(string? path) =>
        // A path the platform itself rejects answers null rather than throwing: this runs on the way to starting a
        // session, and an unusable path is an answer, not a failure.
        Assert.Null(DirectoryPath.Normalize(path));

    [Fact]
    public void IsWithin_TheFolderItself_Counts() =>
        Assert.True(DirectoryPath.IsWithin("/repos/cockpit", "/repos/cockpit"));

    [Fact]
    public void IsWithin_SomethingInside_Counts() =>
        Assert.True(DirectoryPath.IsWithin("/repos/cockpit/src/Core", "/repos/cockpit"));

    [Fact]
    public void IsWithin_ASiblingSharingAPrefix_DoesNot() =>
        Assert.False(DirectoryPath.IsWithin("/repos/cockpit-plugins", "/repos/cockpit"));

    [Fact]
    public void IsWithin_ARootFolder_ContainsEverything() =>
        // A root keeps its separator through Normalize — a root without one is not a path — so the containment test
        // must not insist on a second one, or a project scoped to a drive would claim nothing at all.
        Assert.True(DirectoryPath.IsWithin(Path.GetFullPath("/home/foo"), Path.GetFullPath("/")));

    [Fact]
    public void IsWithin_TheOtherWayRound_DoesNot() =>
        Assert.False(DirectoryPath.IsWithin("/repos", "/repos/cockpit"));

    [Theory]
    [InlineData(null, "/repos/cockpit")]
    [InlineData("/repos/cockpit", null)]
    [InlineData("", "")]
    public void IsWithin_WithoutTwoRealFolders_IsNever(string? path, string? folder) =>
        Assert.False(DirectoryPath.IsWithin(path, folder));
}
