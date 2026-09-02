using Cockpit.Core.WorkingPaths;

namespace Cockpit.Core.Tests.WorkingPaths;

/// <summary>
/// The pure recent/favorites logic behind the New-session dialog's working-directory quick-pick: most-recent
/// first with a cap, case-insensitive / trailing-slash-insensitive de-duplication, and pin/unpin of favorites.
/// </summary>
public class WorkingPathHistoryTests
{
    [Fact]
    public void WithRecent_PutsThePathAtTheFront()
    {
        var history = WorkingPathHistory.Empty
            .WithRecent(@"C:\a")
            .WithRecent(@"C:\b");

        Assert.Equal(new[] { @"C:\b", @"C:\a" }, history.Recent);
    }

    [Fact]
    public void WithRecent_MovesAnExistingPathToTheFrontWithoutDuplicating()
    {
        var history = WorkingPathHistory.Empty
            .WithRecent(@"C:\a")
            .WithRecent(@"C:\b")
            .WithRecent(@"C:\a");

        Assert.Equal(new[] { @"C:\a", @"C:\b" }, history.Recent);
    }

    [Fact]
    public void WithRecent_DeDuplicatesCaseInsensitivelyAndIgnoringTrailingSeparators()
    {
        var history = WorkingPathHistory.Empty
            .WithRecent(@"C:\Proj")
            .WithRecent(@"c:\proj\");

        Assert.Equal(@"c:\proj\", Assert.Single(history.Recent));
    }

    [Fact]
    public void WithRecent_CapsAtMaxRecent()
    {
        var history = WorkingPathHistory.Empty;
        for (var i = 0; i < WorkingPathHistory.MaxRecent + 5; i++)
        {
            history = history.WithRecent($@"C:\p{i}");
        }

        Assert.Equal(WorkingPathHistory.MaxRecent, System.Linq.Enumerable.Count(history.Recent));
        Assert.Equal($@"C:\p{WorkingPathHistory.MaxRecent + 4}", history.Recent[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithRecent_IgnoresBlankPaths(string? path) =>
        Assert.Empty(WorkingPathHistory.Empty.WithRecent(path).Recent);

    [Fact]
    public void WithFavorite_PinsAndUnpins()
    {
        var pinned = WorkingPathHistory.Empty.WithFavorite(@"C:\fav", favorite: true);
        Assert.Equal(new[] { @"C:\fav" }, pinned.Favorites);
        Assert.True(pinned.IsFavorite(@"c:\fav\"));

        var unpinned = pinned.WithFavorite(@"C:\fav", favorite: false);
        Assert.Empty(unpinned.Favorites);
        Assert.False(unpinned.IsFavorite(@"C:\fav"));
    }

    [Fact]
    public void WithFavorite_PinningTwiceDoesNotDuplicate()
    {
        var history = WorkingPathHistory.Empty
            .WithFavorite(@"C:\fav", favorite: true)
            .WithFavorite(@"c:\fav\", favorite: true);

        Assert.Single(history.Favorites);
    }

    [Fact]
    public void WithRecent_DoesNotAffectFavorites()
    {
        var history = WorkingPathHistory.Empty
            .WithFavorite(@"C:\fav", favorite: true)
            .WithRecent(@"C:\other");

        Assert.Equal(new[] { @"C:\fav" }, history.Favorites);
        Assert.Equal(new[] { @"C:\other" }, history.Recent);
    }

    [Fact]
    public void WithoutPath_RemovesFromBothRecentAndFavorites()
    {
        // A path can sit in both lists (a pinned favorite that was also just used); the ✕ forgets it wherever it is,
        // case- and trailing-separator-insensitively like the rest of the history.
        var history = new WorkingPathHistory([@"C:\proj", @"C:\other"], [@"C:\proj"])
            .WithoutPath(@"c:\proj\");

        Assert.Equal(new[] { @"C:\other" }, history.Recent);
        Assert.Empty(history.Favorites);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutPath_IgnoresBlankPaths(string? path)
    {
        var history = new WorkingPathHistory([@"C:\a"], [@"C:\fav"]);

        Assert.Equivalent(history, history.WithoutPath(path));
    }
}
