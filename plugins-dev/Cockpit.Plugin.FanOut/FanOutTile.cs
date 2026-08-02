namespace Cockpit.Plugin.FanOut;

// Where one fan-out session sits on the workspace's tile grid. `ColumnSpan` is what keeps a row
// that holds fewer tiles than the grid has columns from leaving a hole: the row's tiles widen to fill it.
public sealed record FanOutTile(int Column, int Row, int ColumnSpan);
