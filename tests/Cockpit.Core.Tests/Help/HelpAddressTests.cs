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

    // AC-1042: a guide that ships from `docs/` writes ordinary markdown links so GitHub can follow them too;
    // in the app the same spelling has to reach the page next to it.
    [Theory]
    [InlineData("API-REFERENCE.md", "API-REFERENCE", null)]
    [InlineData("API-REFERENCE.md#icockpithost", "API-REFERENCE", "icockpithost")]
    [InlineData("../guides/setup.md#bot-token", "setup", "bot-token")]
    public void FromSiblingLink_ReadsTheFileNameAsTheArticle(string link, string article, string? section)
    {
        var address = HelpAddress.FromSiblingLink(link);

        Assert.NotNull(address);
        Assert.Equal(article, address!.Article);
        Assert.Equal(section, address.Section);
    }

    // Everything else belongs to the browser: a page must not be able to turn an address on the internet into
    // navigation inside the window.
    [Theory]
    [InlineData("https://example.com/setup.md")]
    [InlineData("//example.com/setup.md")]
    [InlineData("example-store-index.json")]
    [InlineData("")]
    public void FromSiblingLink_IgnoresWhatIsNotAPageBesideIt(string link)
    {
        Assert.Null(HelpAddress.FromSiblingLink(link));
    }
}
