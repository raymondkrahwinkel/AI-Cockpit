using Cockpit.Core.Voice;
using FluentAssertions;

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
        TtsProseExtractor.Extract(string.Empty).Should().BeEmpty();
        TtsProseExtractor.Extract("   \n  ").Should().BeEmpty();
    }

    [Fact]
    public void Extract_Paragraph_SplitsIntoIndividualSentences()
    {
        var sentences = TtsProseExtractor.Extract("The build is green. Tests pass! What now?");

        sentences.Should().Equal("The build is green.", "Tests pass!", "What now?");
    }

    [Fact]
    public void Extract_StripsEmojiAndPictographs_KeepingTheSurroundingProse()
    {
        var sentences = TtsProseExtractor.Extract("Goedenavond Raymond 🌙 alles is groen ✅ en gepusht.");

        sentences.Should().ContainSingle().Which.Should().Be("Goedenavond Raymond alles is groen en gepusht.");
    }

    [Fact]
    public void Extract_StripsAJoinedEmojiSequence_LeavingNoLeftoverJoinersOrSkinTones()
    {
        var sentences = TtsProseExtractor.Extract("Klaar 👍🏽 en verzonden.");

        sentences.Should().ContainSingle().Which.Should().Be("Klaar en verzonden.");
    }

    [Fact]
    public void Extract_KeepsCurrencyAndMathSymbols_WhichCarrySpokenMeaning()
    {
        var sentences = TtsProseExtractor.Extract("Het kost €5 en 2 + 2 = 4.");

        sentences.Should().ContainSingle().Which.Should().Be("Het kost €5 en 2 + 2 = 4.");
    }

    [Fact]
    public void Extract_SkipsFencedCodeBlocks()
    {
        var markdown = "Here is the fix.\n\n```csharp\nDockPanel.SetDock(topBar, Dock.Top);\n```\n\nDone.";

        var sentences = TtsProseExtractor.Extract(markdown);

        sentences.Should().Equal("Here is the fix.", "Done.");
        string.Join(" ", sentences).Should().NotContain("DockPanel");
    }

    [Fact]
    public void Extract_SkipsTables()
    {
        var markdown = "Summary below.\n\n| Repo | Status |\n|------|--------|\n| Cockpit | active |\n\nThat is all.";

        var sentences = TtsProseExtractor.Extract(markdown);

        sentences.Should().Equal("Summary below.", "That is all.");
        string.Join(" ", sentences).Should().NotContain("Repo").And.NotContain("|");
    }

    [Fact]
    public void Extract_HeadingAndListItems_EachReadAsItsOwnSentence()
    {
        var markdown = "## What changed\n\n- Fixed the layout bug\n- Added a test";

        var sentences = TtsProseExtractor.Extract(markdown);

        sentences.Should().Equal("What changed.", "Fixed the layout bug.", "Added a test.");
    }

    [Fact]
    public void Extract_StripsInlineMarkdownMarkup()
    {
        var sentences = TtsProseExtractor.Extract("This is **bold** and `code` and *italic*.");

        sentences.Should().Equal("This is bold and code and italic.");
    }

    [Fact]
    public void Extract_ReplacesPathsAndUrlsWithNaturalWords()
    {
        var sentences = TtsProseExtractor.Extract("I edited `C:\\Users\\raymo\\Notes.md` — see https://example.com/docs for the /home/raymond/config path.");

        sentences.Should().ContainSingle();
        sentences[0].Should().NotContain("C:\\").And.NotContain("https://").And.NotContain("/home/raymond");
        sentences[0].Should().Contain("a path").And.Contain("a link");
    }
}
