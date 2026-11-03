using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Profiles;

/// <summary>
/// The pure rules of a profile's spawn environment variables (AC-22): how the list becomes the overlay the
/// spawn paths consume.
/// </summary>
public class ProfileEnvironmentVariableTests
{
    // The spawn composition (TtyEnvironment, the Claude driver's environment) folds case-insensitively, so two
    // case-variant keys are one variable there — the overlay must collapse them the same way, deterministically,
    // instead of leaving the collision to whichever dictionary sees them last.
    [Fact]
    public void ToOverlay_CaseVariantKeysAreOneVariable_TheLaterEntryWins()
    {
        var overlay = ProfileEnvironmentVariable.ToOverlay(
        [
            new("MyVar", "/first"),
            new("MYVAR", "/second"),
        ]);

        Assert.Single(overlay);
        Assert.Contains("/second", overlay.Values);
    }
}
