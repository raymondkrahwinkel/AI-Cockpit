namespace Cockpit.Plugin.FanOut;

// The tile grid a fan-out's sessions are placed on: the number of equal columns and rows to build, and where
// each session goes. Produced by `FanOutTileLayout.For`.
public sealed record FanOutLayout(int Columns, int Rows, IReadOnlyList<FanOutTile> Tiles);
