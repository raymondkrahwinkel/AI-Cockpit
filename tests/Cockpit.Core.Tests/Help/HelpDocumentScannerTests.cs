using System.Reflection;
using Cockpit.Core.Help;

namespace Cockpit.Core.Tests.Help;

/// <summary>
/// The discovery convention (AC-1033): documentation is whatever landed under a <c>Docs</c> folder in an
/// assembly, found by scanning rather than by a list. Runs against this test assembly's own embedded
/// fixtures, so it exercises the real resource-naming path a plugin's build produces.
/// </summary>
public class HelpDocumentScannerTests
{
    private static readonly Assembly Fixtures = typeof(HelpDocumentScannerTests).Assembly;

    private static readonly HelpOwner Plugin = new("gitlab-notifier", "GitLab Notifier", "Marijn de Groot");

    [Fact]
    public void Scan_FindsEveryPageWithoutAManifest()
    {
        var keys = HelpDocumentScanner.Scan(Fixtures, HelpOwner.Core).Select(article => article.Key);

        Assert.Equal(["welcome", "worktrees"], keys.Order());
    }

    [Fact]
    public void Scan_ReadsTitleOrderAndSummaryFromFrontMatter()
    {
        var welcome = _Core("welcome");

        Assert.Equal("Welcome", welcome.Title);
        Assert.Equal(10, welcome.Order);
        Assert.Equal("What this window is for.", welcome.Summary);
        Assert.Equal("👋", welcome.Icon);
    }

    [Fact]
    public void Scan_PlacesCorePagesInTheCategoryTheyDeclare()
    {
        Assert.Equal(HelpCategory.General, _Core("welcome").Category);
        Assert.Equal(HelpCategory.System, _Core("worktrees").Category);
    }

    [Fact]
    public void Scan_StripsFrontMatterFromTheRenderedBody()
    {
        Assert.DoesNotContain("title:", _Core("welcome").Markdown, StringComparison.Ordinal);
        Assert.StartsWith("An opening paragraph", _Core("welcome").Markdown, StringComparison.Ordinal);
    }

    // The load-bearing half of the ownership rule: a plugin cannot place itself outside `Plugins`, however
    // its front matter is written. `worktrees.md` asks for `system` and still lands under `Plugins`.
    [Fact]
    public void Scan_KeepsAPluginUnderPluginsWhateverItsFrontMatterAsks()
    {
        var articles = HelpDocumentScanner.Scan(Fixtures, Plugin);

        Assert.All(articles, article => Assert.Equal(HelpCategory.Plugins, article.Category));
    }

    [Fact]
    public void Scan_PrefixesAPluginsArticleIdsWithItsOwnId()
    {
        var articles = HelpDocumentScanner.Scan(Fixtures, Plugin);

        Assert.Contains(articles, article => article.Id == "gitlab-notifier/welcome");
        Assert.Equal("welcome", _Core("welcome").Id);
    }

    [Fact]
    public void Scan_ReturnsNothingForAnAssemblyThatShipsNoDocumentation()
    {
        var none = HelpDocumentScanner.Scan(typeof(HelpArticle).Assembly, Plugin);

        Assert.Empty(none);
    }

    [Fact]
    public void Scan_ShowsTheRequestedLanguageWhenItExists()
    {
        var welcome = _Article(HelpDocumentScanner.Scan(Fixtures, HelpOwner.Core, "nl"), "welcome");

        Assert.Equal("Welkom", welcome.Title);
        Assert.Equal("nl", welcome.Language);
        Assert.False(welcome.IsTranslationMissing);
    }

    // Falling back is not the same as being translated, and the reader is told which one happened.
    [Fact]
    public void Scan_FallsBackToTheDefaultLanguageVisibly()
    {
        var worktrees = _Article(HelpDocumentScanner.Scan(Fixtures, HelpOwner.Core, "nl"), "worktrees");

        Assert.Equal("Worktrees", worktrees.Title);
        Assert.Equal("en", worktrees.Language);
        Assert.True(worktrees.IsTranslationMissing);
    }

    // A component that only ever ships one language should notice nothing about this mechanism.
    [Fact]
    public void Scan_TreatsAFileWithoutALanguageCodeAsTheDefaultLanguage()
    {
        Assert.Equal("en", _Core("worktrees").Language);
        Assert.False(_Core("worktrees").IsTranslationMissing);
    }

    [Fact]
    public void Scan_TakesSectionIdsFromExplicitAnchorsOnly()
    {
        var sections = _Core("welcome").Sections;

        Assert.Equal(["what", "finding"], sections.Select(section => section.Id));
        Assert.Equal("What you are looking at", sections[0].Title);
    }

    // The rule the whole deep-link scheme rests on: the ids are identical in both languages, only the prose
    // moved. Translate the anchors and every link works in exactly one language.
    [Fact]
    public void Scan_KeepsSectionIdsIdenticalAcrossTranslations()
    {
        var english = _Core("welcome").Sections.Select(section => section.Id);
        var dutch = _Article(HelpDocumentScanner.Scan(Fixtures, HelpOwner.Core, "nl"), "welcome")
            .Sections.Select(section => section.Id);

        Assert.Equal(english, dutch);
    }

    [Fact]
    public void Scan_KeepsTextUnderTheSectionItBelongsTo()
    {
        var what = _Core("welcome").Sections.Single(section => section.Id == "what");

        Assert.Contains("ships one", what.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Search spans", what.Text, StringComparison.Ordinal);
    }

    private static HelpArticle _Core(string key) =>
        _Article(HelpDocumentScanner.Scan(Fixtures, HelpOwner.Core), key);

    private static HelpArticle _Article(IReadOnlyList<HelpArticle> articles, string key) =>
        articles.Single(article => article.Key == key);
}
