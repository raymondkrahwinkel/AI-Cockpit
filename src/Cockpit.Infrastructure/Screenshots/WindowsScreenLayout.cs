using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Windows' virtual screen (AC-327): the rectangle that spans every monitor, and where each of them sits inside
/// it.
/// </summary>
/// <remarks>
/// The virtual screen is not the union of the monitors — Windows defines it as their bounding rectangle, which
/// on a staggered or L-shaped arrangement contains area no monitor covers. A capture of it therefore has holes
/// in it, and the displays are what say which pixels are anyone's.
/// </remarks>
internal sealed record WindowsScreenLayout
{
    /// <summary>The whole virtual screen, in the coordinates the monitors below are reported in.</summary>
    public required CaptureRect VirtualBounds { get; init; }

    /// <summary>Every monitor Windows currently reports, in enumeration order.</summary>
    public required IReadOnlyList<DesktopDisplay> Displays { get; init; }
}
