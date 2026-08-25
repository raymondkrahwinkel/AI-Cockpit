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

    /// <summary>
    /// AC-1074: the live config held "D0BNYEX539D" — a DM conversation id in the member-id field. Telling the
    /// operator which object they actually pasted is what stops them pasting it straight back in.
    /// </summary>
    [Theory]
    [InlineData("D0BNYEX539D", "a DM conversation id")]
    [InlineData("C0BRZNHGFEJ", "a public channel id")]
    [InlineData("G0123ABCDEF", "a private channel id")]
    [InlineData("B0123ABCDEF", "a bot id")]
    [InlineData("T0123ABCDEF", "a workspace id")]
    public void AnotherSlackObjectId_IsNamedForWhatItActuallyIs(string userId, string expected)
    {
        var error = SlackUserId.Validate(userId);

        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.Ordinal);
        Assert.Contains("not a Slack member id", error, StringComparison.Ordinal);
    }

    // AC-1074: what SlackChannelPlugin checks a stored access list with at load, not only at save.
    [Fact]
    public void ValidateAll_ReportsTheFirstBadIdAndPassesACleanList()
    {
        Assert.Null(SlackUserId.ValidateAll(["U0123ABCDE", "W0123ABCDE"]));
        Assert.Contains("a DM conversation id", SlackUserId.ValidateAll(["U0123ABCDE", "D0BNYEX539D"])!, StringComparison.Ordinal);
    }
}
