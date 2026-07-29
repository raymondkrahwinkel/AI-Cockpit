using FluentAssertions;
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
        ProjectResourcePathPortability.ToStoredReference(_Root, picked).Should().Be("docs/handbook.md");
    }

    /// <summary>AC-485 review (FIX 5): a nested relative path must carry no platform-specific separator either — not just a single-segment one.</summary>
    [Fact]
    public void ToStoredReference_APathNestedSeveralFoldersDeep_UsesForwardSlashesThroughout()
    {
        var picked = _Under("docs", "handbook", "team", "onboarding.md");

        ProjectResourcePathPortability.ToStoredReference(_Root, picked).Should().Be("docs/handbook/team/onboarding.md");
    }

    [Fact]
    public void ToStoredReference_ThePickedFolderItself_BecomesTheCurrentDirectoryMarker() =>
        ProjectResourcePathPortability.ToStoredReference(_Root, _Root).Should().Be(".");

    [Fact]
    public void ToStoredReference_APathOutsideSourceDirectory_StaysAbsolute() =>
        ProjectResourcePathPortability.ToStoredReference(_Root, _Outside).Should().Be(_Outside);

    [Fact]
    public void ToStoredReference_NoSourceDirectory_StaysAbsolute() =>
        ProjectResourcePathPortability.ToStoredReference(null, _Outside).Should().Be(_Outside);

    /// <summary>A trailing separator on the folder must not defeat the "is it under here" check.</summary>
    [Fact]
    public void ToStoredReference_SourceDirectoryWithATrailingSeparator_StillMatches()
    {
        var picked = _Under("docs", "handbook.md");

        ProjectResourcePathPortability.ToStoredReference(_Root + Path.DirectorySeparatorChar, picked)
            .Should().Be("docs/handbook.md");
    }

    /// <summary>A scheme reference is a plugin's identifier, not a path — never rewritten, whatever it looks like.</summary>
    [Fact]
    public void ToStoredReference_ASchemeReference_IsNeverTouched() =>
        ProjectResourcePathPortability.ToStoredReference(_Root, "depot:cockpit").Should().Be("depot:cockpit");

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

        var act = () => ProjectResourcePathPortability.ToStoredReference(_Root, malformed);

        act.Should().NotThrow();
        ProjectResourcePathPortability.ToStoredReference(_Root, malformed).Should().Be(malformed);
    }

    [Fact]
    public void IsMachineBound_APathInsideSourceDirectory_IsFalse() =>
        ProjectResourcePathPortability.IsMachineBound(_Root, _Under("docs", "handbook.md")).Should().BeFalse();

    [Fact]
    public void IsMachineBound_APathOutsideSourceDirectory_IsTrue() =>
        ProjectResourcePathPortability.IsMachineBound(_Root, _Outside).Should().BeTrue();

    [Fact]
    public void IsMachineBound_ARelativeReference_IsFalse() =>
        ProjectResourcePathPortability.IsMachineBound(_Root, Path.Combine("docs", "handbook.md")).Should().BeFalse();

    [Fact]
    public void IsMachineBound_ASchemeReference_IsFalse() =>
        ProjectResourcePathPortability.IsMachineBound(_Root, "depot:cockpit").Should().BeFalse();

    [Fact]
    public void IsMachineBound_NoSourceDirectory_TreatsAnyAbsolutePathAsMachineBound() =>
        ProjectResourcePathPortability.IsMachineBound(null, _Outside).Should().BeTrue();

    [Fact]
    public void IsMachineBound_ABlankReference_IsFalse() =>
        ProjectResourcePathPortability.IsMachineBound(_Root, "").Should().BeFalse();

    /// <summary>AC-485 review (FIX 7): the same NUL-character case <see cref="ToStoredReference_APickedPathWithAnIllegalCharacter_IsStoredAsPickedRatherThanThrowing"/> pins for the sibling method — must fail open (not machine-bound) rather than throw.</summary>
    [Fact]
    public void IsMachineBound_AReferenceWithAnIllegalCharacter_IsFalseRatherThanThrowing()
    {
        var malformed = _Under("docs", "bad\0name.md");

        var act = () => ProjectResourcePathPortability.IsMachineBound(_Root, malformed);

        act.Should().NotThrow();
        ProjectResourcePathPortability.IsMachineBound(_Root, malformed).Should().BeFalse();
    }

    /// <summary>
    /// AC-485 review (FIX 8): pins the platform asymmetry the class doc now writes down rather than "fixes" — a
    /// reference shaped for the platform this test is <em>not</em> running on is never fully qualified as far as
    /// <see cref="Path.IsPathFullyQualified(string)"/> is concerned, so it can never be judged machine-bound here,
    /// however far outside the project folder it plainly is on the platform that authored it.
    /// </summary>
    [Fact]
    public void IsMachineBound_AReferenceShapedForTheOtherPlatform_IsNeverMachineBound()
    {
        var otherPlatformPath = OperatingSystem.IsWindows()
            ? "/home/raymond/Elsewhere/notes"
            : @"C:\Users\raymond\Elsewhere\notes";

        ProjectResourcePathPortability.IsMachineBound(_Root, otherPlatformPath).Should().BeFalse();
    }
}
