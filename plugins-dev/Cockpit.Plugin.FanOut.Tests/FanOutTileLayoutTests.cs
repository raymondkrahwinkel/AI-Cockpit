namespace Cockpit.Plugin.FanOut.Tests;

public class FanOutTileLayoutTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 6, 2)]
    public void For_TwoToFiveArms_KeepsRowsAtMostThreeWide(int count, int columns, int rows)
    {
        var layout = FanOutTileLayout.For(count);

        Assert.Equal(columns, layout.Columns);
        Assert.Equal(rows, layout.Rows);
        Assert.Equal(count, layout.Tiles.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void For_AnyArmCount_FillsEveryRowEdgeToEdge(int count)
    {
        var layout = FanOutTileLayout.For(count);

        foreach (var row in layout.Tiles.GroupBy(tile => tile.Row))
        {
            var ordered = row.OrderBy(tile => tile.Column).ToList();

            Assert.Equal(0, ordered[0].Column);
            Assert.Equal(layout.Columns, ordered[^1].Column + ordered[^1].ColumnSpan);
            Assert.All(ordered.Zip(ordered.Skip(1)), pair => Assert.Equal(pair.First.Column + pair.First.ColumnSpan, pair.Second.Column));
        }
    }

    [Fact]
    public void For_FiveArms_PutsThreeOnTopAndTwoBelow()
    {
        var layout = FanOutTileLayout.For(5);

        Assert.Equal(3, layout.Tiles.Count(tile => tile.Row == 0));
        Assert.Equal(2, layout.Tiles.Count(tile => tile.Row == 1));
        Assert.All(layout.Tiles.Where(tile => tile.Row == 0), tile => Assert.Equal(2, tile.ColumnSpan));
        Assert.All(layout.Tiles.Where(tile => tile.Row == 1), tile => Assert.Equal(3, tile.ColumnSpan));
    }

    [Fact]
    public void For_NoArms_YieldsAnEmptyGridRatherThanDividingByZero()
    {
        var layout = FanOutTileLayout.For(0);

        Assert.Empty(layout.Tiles);
        Assert.Equal(1, layout.Columns);
        Assert.Equal(1, layout.Rows);
    }
}
