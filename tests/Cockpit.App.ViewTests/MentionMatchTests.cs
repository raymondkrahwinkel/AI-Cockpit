using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-740's picker row: splitting a ranked path into what the row template shows — bold name, dimmed parent,
/// and the trailing '/' that marks a directory both in the row and in the inserted mention.
/// </summary>
[Collection("avalonia")]
public class MentionMatchTests
{
    // One split, one table. Four facts stood here reading the same four properties off the same constructor;
    // the root-level file row asserted only the parent, and now carries the whole answer like every other row.
    [Theory]
    //          path                          isDirectory  fileName              parent       displayName
    [InlineData("src/Views/SessionView.axaml", false, "SessionView.axaml", "src/Views", "SessionView.axaml")]
    [InlineData("Program.cs", false, "Program.cs", "", "Program.cs")]
    // The trailing '/' is what marks a directory, and the display name keeps it — in the row and in the mention.
    [InlineData("src/Views/", true, "Views", "src", "Views/")]
    [InlineData("src/", true, "src", "", "src/")]
    public void APath_SplitsIntoWhatTheRowShows(
        string path, bool isDirectory, string fileName, string parentDirectory, string displayName)
    {
        var match = new MentionMatch(path);

        Assert.Equal(isDirectory, match.IsDirectory);
        Assert.Equal(fileName, match.FileName);
        Assert.Equal(parentDirectory, match.ParentDirectory);
        Assert.Equal(displayName, match.DisplayName);
    }
}
