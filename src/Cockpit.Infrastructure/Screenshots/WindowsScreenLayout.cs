using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// Windows' virtual screen (AC-327): the rectangle that spans every monitor, and where each of them sits inside
// it.
// The virtual screen is not the union of the monitors — Windows defines it as their bounding rectangle, which
// on a staggered or L-shaped arrangement contains area no monitor covers. A capture of it therefore has holes
// in it, and the displays are what say which pixels are anyone's.
internal sealed record WindowsScreenLayout
{
    // The whole virtual screen, in the coordinates the monitors below are reported in.
    public required CaptureRect VirtualBounds { get; init; }

    // Every monitor Windows currently reports, in enumeration order.
    public required IReadOnlyList<DesktopDisplay> Displays { get; init; }
}
