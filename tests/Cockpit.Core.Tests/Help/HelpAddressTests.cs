using Cockpit.Core.Help;

namespace Cockpit.Core.Tests.Help;

/// <summary>
/// The one spelling a deep link uses, from the app, from a plugin and from a <c>help:</c> link inside the
/// documentation itself (AC-1033).
/// </summary>
public class HelpAddressTests
{
    [Theory]
    [InlineData("welcome", "welcome", null)]
    [InlineData("welcome#finding", "welcome", "finding")]
    [InlineData("discord/setup#bot-token", "discord/setup", "bot-token")]
    [InlineData("  welcome # finding ", "welcome", "finding")]
    public void Parse_SplitsArticleFromSection(string input, string article, string? section)
    {
        var address = HelpAddress.Parse(input);

        Assert.Equal(article, address.Article);
        Assert.Equal(section, address.Section);
    }

    // An address with a trailing hash and nothing after it means the article, not a section named "".
    [Fact]
    public void Parse_TreatsAnEmptySectionAsNone()
    {
        Assert.Null(HelpAddress.Parse("welcome#").Section);
    }

    [Fact]
    public void ToString_RoundTripsWhatWasParsed()
    {
        Assert.Equal("discord/setup#bot-token", HelpAddress.Parse("discord/setup#bot-token").ToString());
        Assert.Equal("welcome", HelpAddress.Parse("welcome").ToString());
    }
}
