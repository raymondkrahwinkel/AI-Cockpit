using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// A project definition travels — that is the whole point of a <c>SourceDirectory</c> shared through git — but an
/// absolute path only ever means something on the machine it was picked on (AC-485). Rooted per platform
/// (<see cref="OperatingSystem.IsWindows"/>) rather than hard-coded, since this repo's CI runs on Linux while local
/// development here runs on Windows, and what counts as a "fully qualified path" is itself platform-specific
/// (the same asymmetry <c>ProjectResourceProbe</c>'s own remarks describe).
/// </summary>
public class ProjectResourcePathPortabilityTests
{
    private static readonly string _Root = OperatingSystem.IsWindows() ? @"C:\Users\raymond\Cockpit" : "/home/raymond/Cockpit";

    private static string _Under(params string[] segments) => Path.Combine([_Root, .. segments]);

    private static readonly string _Outside = OperatingSystem.IsWindows() ? @"C:\Users\raymond\Elsewhere\notes" : "/home/raymond/Elsewhere/notes";

    [Fact]
    public void ToStoredReference_APathInsideSourceDirectory_BecomesRelative()
    {
        var picked = _Under("docs", "handbook.md");

        // AC-485 review (FIX 5): "docs/handbook.md", not Path.Combine("docs", "handbook.md") — the stored value must
        // read the same on every platform, the same way git itself always stores "/" in a tree entry.
        Assert.Equal("docs/handbook.md", ProjectResourcePathPortability.ToStoredReference(_Root, picked));
    }

    /// <summary>AC-485 review (FIX 5): a nested relative path must carry no platform-specific separator either — not just a single-segment one.</summary>
    [Fact]
    public void ToStoredReference_APathNestedSeveralFoldersDeep_UsesForwardSlashesThroughout()
    {
        var picked = _Under("docs", "handbook", "team", "onboarding.md");

        Assert.Equal("docs/handbook/team/onboarding.md", ProjectResourcePathPortability.ToStoredReference(_Root, picked));
    }

    [Fact]
    public void ToStoredReference_ThePickedFolderItself_BecomesTheCurrentDirectoryMarker() =>
        Assert.Equal(".", ProjectResourcePathPortability.ToStoredReference(_Root, _Root));

    [Fact]
    public void ToStoredReference_APathOutsideSourceDirectory_StaysAbsolute() =>
        Assert.Equal(_Outside, ProjectResourcePathPortability.ToStoredReference(_Root, _Outside));

    [Fact]
    public void ToStoredReference_NoSourceDirectory_StaysAbsolute() =>
        Assert.Equal(_Outside, ProjectResourcePathPortability.ToStoredReference(null, _Outside));

    /// <summary>A trailing separator on the folder must not defeat the "is it under here" check.</summary>
    [Fact]
    public void ToStoredReference_SourceDirectoryWithATrailingSeparator_StillMatches()
    {
        var picked = _Under("docs", "handbook.md");

        Assert.Equal("docs/handbook.md",
            ProjectResourcePathPortability.ToStoredReference(_Root + Path.DirectorySeparatorChar, picked));
    }

    /// <summary>A scheme reference is a plugin's identifier, not a path — never rewritten, whatever it looks like.</summary>
    [Fact]
    public void ToStoredReference_ASchemeReference_IsNeverTouched() =>
        Assert.Equal("depot:cockpit", ProjectResourcePathPortability.ToStoredReference(_Root, "depot:cockpit"));

    /// <summary>
    /// AC-485 review (FIX 7): <c>Path.GetFullPath</c> throws <see cref="ArgumentException"/> for a path containing a
    /// NUL character — reachable from a hand-edited <c>cockpit.json</c>, not only from the picker (this method is
    /// also reached from a freshly typed reference, but a NUL cannot reach it that way from the UI). Must fail open
    /// (store verbatim) rather than let that exception surface.
    /// </summary>
    [Fact]
    public void ToStoredReference_APickedPathWithAnIllegalCharacter_IsStoredAsPickedRatherThanThrowing()
    {
        var malformed = _Under("docs", "bad\0name.md");

        var exception = Record.Exception(() => ProjectResourcePathPortability.ToStoredReference(_Root, malformed));

        Assert.Null(exception);
        Assert.Equal(malformed, ProjectResourcePathPortability.ToStoredReference(_Root, malformed));
    }

    // --- ClassifyScope (AC-605 criterion 6: renamed from IsMachineBound, no longer takes SourceDirectory — see
    //     SuggestRepoRelativeFix for the half of the old behavior that needed one) --------------------------------

    [Fact]
    public void ClassifyScope_ARelativeReference_IsRepo() =>
        Assert.Equal(ProjectResourceScope.Repo, ProjectResourcePathPortability.ClassifyScope(Path.Combine("docs", "handbook.md")));

    [Fact]
    public void ClassifyScope_AnAbsolutePath_IsMachine() =>
        Assert.Equal(ProjectResourceScope.Machine, ProjectResourcePathPortability.ClassifyScope(_Outside));

    [Fact]
    public void ClassifyScope_ASchemeReference_IsInstance() =>
        Assert.Equal(ProjectResourceScope.Instance, ProjectResourcePathPortability.ClassifyScope("depot:cockpit"));

    [Theory]
    [InlineData("~")]
    [InlineData("~/Notes/handbook.md")]
    [InlineData("~//Notes/handbook.md")]
    public void ClassifyScope_AHomeAnchoredReference_IsHome(string reference) =>
        Assert.Equal(ProjectResourceScope.Home, ProjectResourcePathPortability.ClassifyScope(reference));

    /// <summary>Raymond's decision (AC-605): "~user/" is not a supported anchor form — .NET has no notion of a POSIX shell's "someone else's home" expansion, so this is left as ordinary (repo-relative-shaped) text rather than guessed at.</summary>
    [Theory]
    [InlineData("~henk/x")]
    [InlineData("~x")]
    public void ClassifyScope_ATildeReferenceThatIsNotSupportedAnchorForm_IsRepoNotHome(string reference) =>
        Assert.Equal(ProjectResourceScope.Repo, ProjectResourcePathPortability.ClassifyScope(reference));

    [Fact]
    public void ClassifyScope_ABlankReference_IsNull() =>
        Assert.Null(ProjectResourcePathPortability.ClassifyScope(""));

    [Fact]
    public void ClassifyScope_Null_IsNull() =>
        Assert.Null(ProjectResourcePathPortability.ClassifyScope(null));

    /// <summary>AC-485 review (FIX 7)'s malformed-reference case, mirrored for the renamed method: must fail open (null) rather than throw.</summary>
    [Fact]
    public void ClassifyScope_AReferenceWithAnIllegalCharacter_IsNullRatherThanThrowing()
    {
        var malformed = _Under("docs", "bad\0name.md");

        var exception = Record.Exception(() => ProjectResourcePathPortability.ClassifyScope(malformed));

        Assert.Null(exception);
    }

    /// <summary>
    /// AC-485 review (FIX 8): pins the platform asymmetry the class doc now writes down rather than "fixes" — a
    /// reference shaped for the platform this test is <em>not</em> running on is never fully qualified as far as
    /// <see cref="Path.IsPathFullyQualified(string)"/> is concerned, so it reads as <see cref="ProjectResourceScope.Repo"/>
    /// here, however far outside the project folder it plainly is on the platform that authored it.
    /// </summary>
    [Fact]
    public void ClassifyScope_AReferenceShapedForTheOtherPlatform_IsRepoNotMachine()
    {
        var otherPlatformPath = OperatingSystem.IsWindows()
            ? "/home/raymond/Elsewhere/notes"
            : @"C:\Users\raymond\Elsewhere\notes";

        Assert.Equal(ProjectResourceScope.Repo, ProjectResourcePathPortability.ClassifyScope(otherPlatformPath));
    }

    // --- SuggestRepoRelativeFix (AC-605 criterion 5) ----------------------------------------------------------

    [Fact]
    public void SuggestRepoRelativeFix_AnAbsolutePathInsideSourceDirectory_SuggestsItsRepoRelativeForm() =>
        Assert.Equal("docs/handbook.md",
            ProjectResourcePathPortability.SuggestRepoRelativeFix(_Root, _Under("docs", "handbook.md")));

    [Fact]
    public void SuggestRepoRelativeFix_AnAbsolutePathOutsideSourceDirectory_IsNull() =>
        Assert.Null(ProjectResourcePathPortability.SuggestRepoRelativeFix(_Root, _Outside));

    [Fact]
    public void SuggestRepoRelativeFix_ARelativeReference_IsNull() =>
        Assert.Null(ProjectResourcePathPortability.SuggestRepoRelativeFix(_Root, Path.Combine("docs", "handbook.md")));

    [Fact]
    public void SuggestRepoRelativeFix_ASchemeReference_IsNull() =>
        Assert.Null(ProjectResourcePathPortability.SuggestRepoRelativeFix(_Root, "depot:cockpit"));

    [Fact]
    public void SuggestRepoRelativeFix_AHomeAnchoredReference_IsNull() =>
        Assert.Null(ProjectResourcePathPortability.SuggestRepoRelativeFix(_Root, "~/Notes/handbook.md"));

    [Fact]
    public void SuggestRepoRelativeFix_NoSourceDirectory_IsNull() =>
        Assert.Null(ProjectResourcePathPortability.SuggestRepoRelativeFix(null, _Under("docs", "handbook.md")));

    [Fact]
    public void SuggestRepoRelativeFix_ABlankReference_IsNull() =>
        Assert.Null(ProjectResourcePathPortability.SuggestRepoRelativeFix(_Root, ""));

    [Fact]
    public void SuggestRepoRelativeFix_AnIllegalCharacter_IsNullRatherThanThrowing()
    {
        var malformed = _Under("docs", "bad\0name.md");

        var exception = Record.Exception(() => ProjectResourcePathPortability.SuggestRepoRelativeFix(_Root, malformed));

        Assert.Null(exception);
        Assert.Null(ProjectResourcePathPortability.SuggestRepoRelativeFix(_Root, malformed));
    }

    // --- IsHomeAnchored / ResolveHomeAnchor (AC-605 criterion 1) ------------------------------------------------

    [Theory]
    [InlineData("~")]
    [InlineData("~/")]
    [InlineData("~/Notes")]
    [InlineData("~//Notes")]
    public void IsHomeAnchored_ASupportedAnchorForm_IsTrue(string reference) =>
        Assert.True(ProjectResourcePathPortability.IsHomeAnchored(reference));

    [Theory]
    [InlineData("~henk/x")]
    [InlineData("~x")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("docs/handbook.md")]
    public void IsHomeAnchored_ANotSupportedForm_IsFalse(string? reference) =>
        Assert.False(ProjectResourcePathPortability.IsHomeAnchored(reference));

    [Fact]
    public void ResolveHomeAnchor_BareTilde_IsTheHomeDirectory() =>
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ProjectResourcePathPortability.ResolveHomeAnchor("~"));

    [Fact]
    public void ResolveHomeAnchor_TildeSlash_IsTheHomeDirectory() =>
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ProjectResourcePathPortability.ResolveHomeAnchor("~/"));

    [Fact]
    public void ResolveHomeAnchor_TildeSlashSuffix_IsUnderTheHomeDirectory() =>
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Notes", "x.md"),
            ProjectResourcePathPortability.ResolveHomeAnchor("~/Notes/x.md"));

    /// <summary>A doubled separator right after the anchor must not be read as an absolute path that replaces home outright (the Path.Combine(home, "/x") footgun).</summary>
    [Fact]
    public void ResolveHomeAnchor_TildeDoubleSlashSuffix_IsStillUnderTheHomeDirectory() =>
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Notes"),
            ProjectResourcePathPortability.ResolveHomeAnchor("~//Notes"));

    /// <summary>
    /// AC-605: deliberately not bounds-checked — see the class remarks on why a <c>..</c> segment climbing back out
    /// of home is this method's to resolve, not to reject. On a home directory with at least two path segments
    /// (true of every CI/dev box this repo runs on) this actually leaves the resolved path outside home.
    /// </summary>
    [Fact]
    public void ResolveHomeAnchor_ASuffixThatClimbsOutOfHome_ResolvesWhereverThatLands()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.GetFullPath(Path.Combine(home, "..", "..", "etc", "passwd"));

        Assert.Equal(expected, ProjectResourcePathPortability.ResolveHomeAnchor("~/../../etc/passwd"));
    }

    [Fact]
    public void ResolveHomeAnchor_ANotSupportedTildeForm_IsReturnedUnchanged() =>
        Assert.Equal("~henk/x", ProjectResourcePathPortability.ResolveHomeAnchor("~henk/x"));

    [Fact]
    public void ResolveHomeAnchor_ANonAnchoredReference_IsReturnedUnchanged() =>
        Assert.Equal("docs/handbook.md", ProjectResourcePathPortability.ResolveHomeAnchor("docs/handbook.md"));

    [Fact]
    public void ResolveHomeAnchor_AnIllegalCharacter_IsReturnedUnchangedRatherThanThrowing()
    {
        var malformed = "~/bad\0name.md";

        var exception = Record.Exception(() => ProjectResourcePathPortability.ResolveHomeAnchor(malformed));

        Assert.Null(exception);
        Assert.Equal(malformed, ProjectResourcePathPortability.ResolveHomeAnchor(malformed));
    }
}
