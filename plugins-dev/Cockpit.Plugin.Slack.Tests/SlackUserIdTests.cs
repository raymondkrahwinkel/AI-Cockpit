using Cockpit.Plugin.Slack.Settings;

namespace Cockpit.Plugin.Slack.Tests;

public class SlackUserIdTests
{
    [Theory]
    [InlineData("U0123ABCDE")]
    [InlineData("W0123ABCDE")]
    [InlineData("U012ABC3DEF")]
    public void AMemberId_IsAccepted(string userId) =>
        Assert.True(SlackUserId.IsValid(userId));

    // AC-1048: the exact input that caused the bug — a display name, not a member id.
    [Theory]
    [InlineData("@Raymond Krahwinkel")]
    [InlineData("Raymond Krahwinkel")]
    [InlineData("raymond")]
    [InlineData("117")]
    [InlineData("u0123abcde")]
    [InlineData("U123")]
    [InlineData("")]
    public void AnythingElse_IsRejected(string userId) =>
        Assert.False(SlackUserId.IsValid(userId));

    [Fact]
    public void ARejectedValue_NamesWhatIsExpectedAndWhereToFindIt()
    {
        var error = SlackUserId.Validate("@Raymond Krahwinkel");

        Assert.NotNull(error);
        Assert.Contains("member id", error, StringComparison.Ordinal);
        Assert.Contains("Copy member ID", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidId_ValidatesClean() =>
        Assert.Null(SlackUserId.Validate("U0123ABCDE"));
}
