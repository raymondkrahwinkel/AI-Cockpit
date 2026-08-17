using Avalonia.Controls;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Diagram.Collab;

// What CouplingBarFactory.Build hands back — the pieces a workspace body's own _RefreshCouplingBar needs to keep
// mutating (label text, chip state, button visibility). Wiring Couple/Disconnect's Click and interpreting the
// surface's own coupling type both stay with the caller.
internal sealed record CouplingBarParts(
    Border Bar,
    TextBlock Label,
    TextBlock ReadChip,
    TextBlock EditChip,
    MaterialIcon Pip,
    Button Couple,
    Button Disconnect);
