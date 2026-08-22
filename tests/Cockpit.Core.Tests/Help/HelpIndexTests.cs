using System.Reflection;
using Cockpit.Core.Help;

namespace Cockpit.Core.Tests.Help;

/// <summary>
/// The index every deep link, every <c>?</c> and the search resolve against (AC-1033): one index over the
/// app's pages and every plugin's at once, offline, with images that only ever come out of the assembly the
/// page itself shipped in.
/// </summary>
public class HelpIndexTests
{
    private static readonly Assembly Fixtures = typeof(HelpIndexTests).Assembly;

    private static readonly HelpOwner Plugin = new("gitlab-notifier", "GitLab Notifier", "Marijn de Groot");

    private static HelpIndex _Index() => HelpIndex.Build([
        new HelpDocumentSource(HelpOwner.Core, Fixtures),
        new HelpDocumentSource(Plugin, Fixtures),
    ]);

    [Fact]
    public void Contains_AcceptsAnArticleAndOneOfItsSections()
    {
        var index = _Index();

        Assert.True(index.Contains(HelpAddress.Parse("welcome")));
        Assert.True(index.Contains(HelpAddress.Parse("welcome#finding")));
    }

    // What keeps a `?` off the screen when it would open nothing, and what the deep-link sweep asserts for
    // every target in the codebase.
    [Fact]
    public void Contains_RejectsAnArticleOrSectionThatDoesNotExist()
    {
        var index = _Index();

        Assert.False(index.Contains(HelpAddress.Parse("slack#interactivity")));
        Assert.False(index.Contains(HelpAddress.Parse("welcome#gone")));
    }

    [Fact]
    public void Search_SpansEveryOwnerAtOnce()
    {
        var owners = _Index().Search("worktree").Select(hit => hit.Article.Owner.Id).Distinct();

        Assert.Equal(["cockpit", "gitlab-notifier"], owners.Order());
    }

    // A hit has to land where the answer is, not at the top of a long page — the same section ids the deep
    // links use, so there is one addressing scheme and not two that can disagree.
    [Fact]
    public void Search_LandsOnTheSection()
    {
        var hit = _Index().Search("isolated checkouts").First(candidate => candidate.Article.Owner.IsCore);

        Assert.Equal("isolation", hit.Section?.Id);
        Assert.Equal("worktrees#isolation", hit.Address.ToString());
    }

    // The section whose heading is the term outranks the page that merely contains it: a heading says what a
    // section is about, a body match says only that the word occurs somewhere on a long page.
    [Fact]
    public void Search_RanksTheSectionNamedAfterTheTermAboveThePageThatMentionsIt()
    {
        var hits = _Index().Search("finding").Where(hit => hit.Article.Owner.IsCore).ToList();

        Assert.Equal("finding", hits[0].Section?.Id);
        Assert.Null(hits[1].Section);
    }

    [Fact]
    public void Search_FindsNothingForAWordThatIsNotThere()
    {
        Assert.Empty(_Index().Search("kubernetes"));
    }

    [Fact]
    public void Search_RequiresEveryTermToBePresent()
    {
        Assert.Empty(_Index().Search("worktree kubernetes"));
    }

    [Fact]
    public void LoadImage_ReturnsBytesForAPictureShippedBesideThePage()
    {
        var index = _Index();
        var image = index.LoadImage(index.Find("welcome")!, "images/shot.png");

        Assert.Equal(HelpImageOutcome.Embedded, image.Outcome);
        Assert.NotEmpty(image.Bytes!);
    }

    // The hard boundary: an external reference is refused, not fetched. Opening a page from a plugin you did
    // not write must not be the moment a stranger's server learns the operator's IP address.
    [Theory]
    [InlineData("https://example.invalid/tracker.png")]
    [InlineData("http://example.invalid/tracker.png")]
    [InlineData("//example.invalid/tracker.png")]
    [InlineData("data:image/png;base64,AAAA")]
    public void LoadImage_BlocksAnythingThatIsNotShippedWithThePage(string reference)
    {
        var index = _Index();

        Assert.Equal(HelpImageOutcome.BlockedExternal, index.LoadImage(index.Find("welcome")!, reference).Outcome);
    }

    [Fact]
    public void LoadImage_ReportsAReferenceThatResolvesToNothing()
    {
        var index = _Index();

        Assert.Equal(HelpImageOutcome.Missing, index.LoadImage(index.Find("welcome")!, "images/absent.png").Outcome);
    }

    [Fact]
    public void LoadImage_PrefersTheDarkVariantWhenThereIsOne()
    {
        var index = _Index();
        var article = index.Find("welcome")!;

        var light = index.LoadImage(article, "images/shot.png");
        var dark = index.LoadImage(article, "images/shot.png", dark: true);

        Assert.Equal(HelpImageOutcome.Embedded, dark.Outcome);
        Assert.NotEqual(light.Bytes!, dark.Bytes!);
    }

    // One image has to stay the ordinary case: a light/dark pair is allowed, never demanded.
    [Fact]
    public void LoadImage_FallsBackToTheOneImageWhenNoDarkVariantWasShipped()
    {
        var index = _Index();
        var article = index.Find("welcome")!;

        Assert.Equal(HelpImageOutcome.Embedded, index.LoadImage(article, "images/plain.png", dark: true).Outcome);
    }

    [Fact]
    public void Build_YieldsAnEmptyIndexForComponentsThatShipNoDocumentation()
    {
        var index = HelpIndex.Build([new HelpDocumentSource(Plugin, typeof(HelpArticle).Assembly)]);

        Assert.Empty(index.Articles);
        Assert.False(index.Contains(HelpAddress.Parse("welcome")));
    }
}
