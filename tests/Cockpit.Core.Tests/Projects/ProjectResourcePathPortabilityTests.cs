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
        Assert.Equal("docs/handbook.md", ProjectResourcePathPortability.SuggestRepoRelativeFix(_Root, picked));
        Assert.Equal(_Outside, ProjectResourcePathPortability.ToStoredReference(_Root, _Outside));
    }

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
}
