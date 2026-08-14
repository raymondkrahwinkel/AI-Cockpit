using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.ViewModels;

// AC-778: a row can carry the images its own message was sent with, kept in memory for the running session so
// the "[+N image]" fragment can reopen them.
public class TranscriptEntryImagesTests
{
    [Fact]
    public void ARowWithoutImagesHasNoChip()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "fix the layout bug");

        Assert.False(entry.HasImages);
        Assert.Equal(string.Empty, entry.ImageChipLabel);
    }

    [Fact]
    public void ARowWithAnEmptyImageListHasNoChip()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "fix the layout bug")
        {
            Images = [],
        };

        Assert.False(entry.HasImages);
    }

    [Fact]
    public void ARowWithOneImageShowsASingularLabel()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "look at this")
        {
            Images = [new ImageAttachment("image/png", "AAAA")],
        };

        Assert.True(entry.HasImages);
        Assert.Equal("[+1 image]", entry.ImageChipLabel);
    }

    [Fact]
    public void ARowWithMultipleImagesShowsAPluralLabel()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "look at these")
        {
            Images = [new ImageAttachment("image/png", "AAAA"), new ImageAttachment("image/png", "BBBB")],
        };

        Assert.True(entry.HasImages);
        Assert.Equal("[+2 images]", entry.ImageChipLabel);
    }

    // `TextWithImageSuffix` is what copy-to-clipboard, session-watch pattern matching and the assistant's
    // read-transcript MCP surface read instead of the bare `Text` — losing the image mention from any of those
    // would be a silent regression (review caught this: `Text` used to carry the suffix itself).
    [Fact]
    public void TextWithImageSuffix_WithoutImages_IsJustTheText()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "just text");

        Assert.Equal("just text", entry.TextWithImageSuffix);
    }

    [Fact]
    public void TextWithImageSuffix_WithImagesAndText_AppendsTheLabel()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "look at this")
        {
            Images = [new ImageAttachment("image/png", "AAAA")],
        };

        Assert.Equal("look at this  [+1 image]", entry.TextWithImageSuffix);
    }

    [Fact]
    public void TextWithImageSuffix_ImageOnlyMessage_IsJustTheLabel()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, string.Empty)
        {
            Images = [new ImageAttachment("image/png", "AAAA")],
        };

        Assert.Equal("[+1 image]", entry.TextWithImageSuffix);
    }
}
