using Cockpit.Plugin.Discord.Settings;

namespace Cockpit.Plugin.Discord.Tests;

public class DiscordUserIdTests
{
    [Theory]
    [InlineData("123456789012345678")]
    [InlineData("12345678901234567")]
    public void ASnowflake_IsAccepted(string userId) =>
        Assert.True(DiscordUserId.IsValid(userId));

    // AC-1048: the same shape of mistake as the Slack bug — a display name or tag, not a snowflake.
    [Theory]
    [InlineData("@Raymond Krahwinkel")]
    [InlineData("Raymond#1234")]
    [InlineData("raymond")]
    [InlineData("117")]
    [InlineData("")]
    public void AnythingElse_IsRejected(string userId) =>
        Assert.False(DiscordUserId.IsValid(userId));

    [Fact]
    public void ARejectedValue_NamesWhatIsExpectedAndWhereToFindIt()
    {
        var error = DiscordUserId.Validate("@Raymond Krahwinkel");

        Assert.NotNull(error);
        Assert.Contains("Discord user id", error, StringComparison.Ordinal);
        Assert.Contains("Copy User ID", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidId_ValidatesClean() =>
        Assert.Null(DiscordUserId.Validate("123456789012345678"));
}
