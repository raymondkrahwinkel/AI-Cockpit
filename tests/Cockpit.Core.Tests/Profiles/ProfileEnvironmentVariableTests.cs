using Cockpit.Core.Profiles;
using FluentAssertions;

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
        ProfileEnvironmentVariable.IsValidKey(key).Should().BeTrue();
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
        ProfileEnvironmentVariable.IsValidKey(key).Should().BeFalse();
    }

    [Fact]
    public void ToOverlay_MapsEachVariableToItsValue()
    {
        var overlay = ProfileEnvironmentVariable.ToOverlay(
        [
            new("AI_OS_ROOT", "/home/raymond/AI-OS"),
            new("MY_TOKEN", "s3cret", IsSecret: true),
        ]);

        overlay.Should().Equal(new Dictionary<string, string?>
        {
            ["AI_OS_ROOT"] = "/home/raymond/AI-OS",
            ["MY_TOKEN"] = "s3cret",
        });
    }

    [Fact]
    public void ToOverlay_WhenAKeyAppearsTwice_TheLaterEntryWins()
    {
        var overlay = ProfileEnvironmentVariable.ToOverlay(
        [
            new("AI_OS_ROOT", "/first"),
            new("AI_OS_ROOT", "/second"),
        ]);

        overlay["AI_OS_ROOT"].Should().Be("/second");
    }
}
