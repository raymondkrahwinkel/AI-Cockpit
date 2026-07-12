using Cockpit.Core.WorkingPaths;
using FluentAssertions;

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

        history.Recent.Should().Equal(@"C:\b", @"C:\a");
    }

    [Fact]
    public void WithRecent_MovesAnExistingPathToTheFrontWithoutDuplicating()
    {
        var history = WorkingPathHistory.Empty
            .WithRecent(@"C:\a")
            .WithRecent(@"C:\b")
            .WithRecent(@"C:\a");

        history.Recent.Should().Equal(@"C:\a", @"C:\b");
    }

    [Fact]
    public void WithRecent_DeDuplicatesCaseInsensitivelyAndIgnoringTrailingSeparators()
    {
        var history = WorkingPathHistory.Empty
            .WithRecent(@"C:\Proj")
            .WithRecent(@"c:\proj\");

        history.Recent.Should().ContainSingle().Which.Should().Be(@"c:\proj\");
    }

    [Fact]
    public void WithRecent_CapsAtMaxRecent()
    {
        var history = WorkingPathHistory.Empty;
        for (var i = 0; i < WorkingPathHistory.MaxRecent + 5; i++)
        {
            history = history.WithRecent($@"C:\p{i}");
        }

        history.Recent.Should().HaveCount(WorkingPathHistory.MaxRecent);
        history.Recent[0].Should().Be($@"C:\p{WorkingPathHistory.MaxRecent + 4}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithRecent_IgnoresBlankPaths(string? path)
        => WorkingPathHistory.Empty.WithRecent(path).Recent.Should().BeEmpty();

    [Fact]
    public void WithFavorite_PinsAndUnpins()
    {
        var pinned = WorkingPathHistory.Empty.WithFavorite(@"C:\fav", favorite: true);
        pinned.Favorites.Should().Equal(@"C:\fav");
        pinned.IsFavorite(@"c:\fav\").Should().BeTrue();

        var unpinned = pinned.WithFavorite(@"C:\fav", favorite: false);
        unpinned.Favorites.Should().BeEmpty();
        unpinned.IsFavorite(@"C:\fav").Should().BeFalse();
    }

    [Fact]
    public void WithFavorite_PinningTwiceDoesNotDuplicate()
    {
        var history = WorkingPathHistory.Empty
            .WithFavorite(@"C:\fav", favorite: true)
            .WithFavorite(@"c:\fav\", favorite: true);

        history.Favorites.Should().ContainSingle();
    }

    [Fact]
    public void WithRecent_DoesNotAffectFavorites()
    {
        var history = WorkingPathHistory.Empty
            .WithFavorite(@"C:\fav", favorite: true)
            .WithRecent(@"C:\other");

        history.Favorites.Should().Equal(@"C:\fav");
        history.Recent.Should().Equal(@"C:\other");
    }
}
