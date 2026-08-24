using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// Windows' virtual screen (AC-327): the bounding rectangle over every monitor, plus where each sits inside it.
// Not the union of the monitors — a staggered/L-shaped arrangement leaves area no monitor covers, so a
// capture of it can have holes; the displays list is what says which pixels belong to anyone.
internal sealed record WindowsScreenLayout
{
    // The whole virtual screen, in the coordinates the monitors below are reported in.
    public required CaptureRect VirtualBounds { get; init; }

    // Every monitor Windows currently reports, in enumeration order.
    public required IReadOnlyList<DesktopDisplay> Displays { get; init; }
}
