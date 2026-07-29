using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="TtsProseExtractor"/> turns an assistant transcript entry's markdown into the sentences
/// worth reading aloud (#35): prose from headings/paragraphs/list items, skipping fenced code and
/// tables, split into individual sentences.
/// </summary>
public class TtsProseExtractorTests
{
    [Fact]
    public void Extract_EmptyOrWhitespace_ReturnsNothing()
    {
        Assert.Empty(TtsProseExtractor.Extract(string.Empty));
        Assert.Empty(TtsProseExtractor.Extract("   \n  "));
    }

    [Fact]
    public void Extract_Paragraph_SplitsIntoIndividualSentences()
    {
        var sentences = TtsProseExtractor.Extract("The build is green. Tests pass! What now?");

        Assert.Equal(new[] { "The build is green.", "Tests pass!", "What now?" }, sentences);
    }

    [Fact]
    public void Extract_StripsEmojiAndPictographs_KeepingTheSurroundingProse()
    {
        var sentences = TtsProseExtractor.Extract("Goedenavond Raymond 🌙 alles is groen ✅ en gepusht.");

        var sentence = Assert.Single(sentences);
        Assert.Equal("Goedenavond Raymond alles is groen en gepusht.", sentence);
    }

    [Fact]
    public void Extract_StripsAJoinedEmojiSequence_LeavingNoLeftoverJoinersOrSkinTones()
    {
        var sentences = TtsProseExtractor.Extract("Klaar 👍🏽 en verzonden.");

        var sentence = Assert.Single(sentences);
        Assert.Equal("Klaar en verzonden.", sentence);
    }

    [Fact]
    public void Extract_KeepsCurrencyAndMathSymbols_WhichCarrySpokenMeaning()
    {
        var sentences = TtsProseExtractor.Extract("Het kost €5 en 2 + 2 = 4.");

        var sentence = Assert.Single(sentences);
        Assert.Equal("Het kost €5 en 2 + 2 = 4.", sentence);
    }

    [Fact]
    public void Extract_SkipsFencedCodeBlocks()
    {
        var markdown = "Here is the fix.\n\n```csharp\nDockPanel.SetDock(topBar, Dock.Top);\n```\n\nDone.";

        var sentences = TtsProseExtractor.Extract(markdown);

        Assert.Equal(new[] { "Here is the fix.", "Done." }, sentences);
        Assert.DoesNotContain("DockPanel", string.Join(" ", sentences));
    }

    [Fact]
    public void Extract_SkipsTables()
    {
        var markdown = "Summary below.\n\n| Repo | Status |\n|------|--------|\n| Cockpit | active |\n\nThat is all.";

        var sentences = TtsProseExtractor.Extract(markdown);

        Assert.Equal(new[] { "Summary below.", "That is all." }, sentences);
        var joined = string.Join(" ", sentences);
        Assert.DoesNotContain("Repo", joined);
        Assert.DoesNotContain("|", joined);
    }

    [Fact]
    public void Extract_HeadingAndListItems_EachReadAsItsOwnSentence()
    {
        var markdown = "## What changed\n\n- Fixed the layout bug\n- Added a test";

        var sentences = TtsProseExtractor.Extract(markdown);

        Assert.Equal(new[] { "What changed.", "Fixed the layout bug.", "Added a test." }, sentences);
    }

    [Fact]
    public void Extract_StripsInlineMarkdownMarkup()
    {
        var sentences = TtsProseExtractor.Extract("This is **bold** and `code` and *italic*.");

        Assert.Equal(new[] { "This is bold and code and italic." }, sentences);
    }

    [Fact]
    public void Extract_ReplacesPathsAndUrlsWithNaturalWords()
    {
        var sentences = TtsProseExtractor.Extract("I edited `C:\\Users\\raymo\\Notes.md` — see https://example.com/docs for the /home/raymond/config path.");

        Assert.Single(sentences);
        Assert.DoesNotContain("C:\\", sentences[0]);
        Assert.DoesNotContain("https://", sentences[0]);
        Assert.DoesNotContain("/home/raymond", sentences[0]);
        Assert.Contains("a path", sentences[0]);
        Assert.Contains("a link", sentences[0]);
    }
}
