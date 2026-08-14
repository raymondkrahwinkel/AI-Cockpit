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
}
