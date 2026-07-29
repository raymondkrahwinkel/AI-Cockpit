using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Profiles;

/// <summary>
/// The pure rules of a profile's spawn environment variables (AC-22): what counts as a settable variable
/// name, and how the list becomes the overlay the spawn paths consume.
/// </summary>
public class ProfileEnvironmentVariableTests
{
    [Theory]
    [InlineData("AI_OS_ROOT")]
    [InlineData("_private")]
    [InlineData("PATH2")]
    [InlineData("x")]
    public void IsValidKey_AcceptsPosixStyleNames(string key)
    {
        Assert.True(ProfileEnvironmentVariable.IsValidKey(key));
    }

    [Theory]
    [InlineData("2LEADING_DIGIT")]
    [InlineData("MY-VAR")]
    [InlineData("A B")]
    [InlineData("A.B")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValidKey_RefusesWhatAShellCouldNotSetEither(string? key)
    {
        Assert.False(ProfileEnvironmentVariable.IsValidKey(key));
    }

    [Fact]
    public void ToOverlay_MapsEachVariableToItsValue()
    {
        var overlay = ProfileEnvironmentVariable.ToOverlay(
        [
            new("AI_OS_ROOT", "/home/raymond/AI-OS"),
            new("MY_TOKEN", "s3cret", IsSecret: true),
        ]);

        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["AI_OS_ROOT"] = "/home/raymond/AI-OS",
                ["MY_TOKEN"] = "s3cret",
            },
            overlay);
    }

    [Fact]
    public void ToOverlay_WhenAKeyAppearsTwice_TheLaterEntryWins()
    {
        var overlay = ProfileEnvironmentVariable.ToOverlay(
        [
            new("AI_OS_ROOT", "/first"),
            new("AI_OS_ROOT", "/second"),
        ]);

        Assert.Equal("/second", overlay["AI_OS_ROOT"]);
    }

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
