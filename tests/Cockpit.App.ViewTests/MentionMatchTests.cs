using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-740's picker row: splitting a ranked path into what the row template shows — bold name, dimmed parent,
/// and the trailing '/' that marks a directory both in the row and in the inserted mention.
/// </summary>
[Collection("avalonia")]
public class MentionMatchTests
{
    [Fact]
    public void AFile_WithADirectory_SplitsNameFromParent()
    {
        var match = new MentionMatch("src/Views/SessionView.axaml");

        Assert.False(match.IsDirectory);
        Assert.Equal("SessionView.axaml", match.FileName);
        Assert.Equal("src/Views", match.ParentDirectory);
        Assert.Equal("SessionView.axaml", match.DisplayName);
    }

    [Fact]
    public void ARootLevelFile_HasNoParent()
    {
        var match = new MentionMatch("Program.cs");

        Assert.Equal(string.Empty, match.ParentDirectory);
    }

    [Fact]
    public void ADirectory_TrailingSlashMarksItAndTheDisplayNameKeepsIt()
    {
        var match = new MentionMatch("src/Views/");

        Assert.True(match.IsDirectory);
        Assert.Equal("Views", match.FileName);
        Assert.Equal("src", match.ParentDirectory);
        Assert.Equal("Views/", match.DisplayName);
    }

    [Fact]
    public void ARootLevelDirectory_HasNoParent()
    {
        var match = new MentionMatch("src/");

        Assert.True(match.IsDirectory);
        Assert.Equal("src", match.FileName);
        Assert.Equal(string.Empty, match.ParentDirectory);
    }
}
