using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-608: binding a chord that another action already holds did nothing visible and nothing useful — the dispatch
/// invokes the first match in catalog order, so the row that lost was decided by list position and said so nowhere.
/// This is the rule that makes two owners impossible in the first place.
/// </summary>
public class ShortcutGestureOwnershipTests
{
    [Fact]
    public void TakingAGestureAnotherRowHolds_TakesItFromThatRow()
    {
        // The reported case: Ctrl+Shift+M is Toggle zoom's default, so every row after it in the catalog lost to it.
        var gestures = new List<string> { "Ctrl+Shift+M", "Ctrl+N", "Ctrl+Shift+M" };

        Assert.Equal([0], ShortcutGestureOwnership.DisplacedBy(gestures, claimantIndex: 2));
    }

    [Fact]
    public void ALoneKeyIsJudgedTheSameWayAChordIs()
    {
        // "M binds, Ctrl+Shift+M does not" was the shape of the report, and the difference was only that nothing
        // else held M. The rule must not grow a modifier-shaped exception out of that.
        var gestures = new List<string> { "M", "M" };

        Assert.Equal([0], ShortcutGestureOwnership.DisplacedBy(gestures, claimantIndex: 1));
    }

    [Fact]
    public void TheSameGestureWrittenTwoWays_IsOneGesture()
    {
        // Comparing text would let "Shift+Ctrl+M" sit beside "Ctrl+Shift+M" as if they were different bindings —
        // the capture field never writes that form, but a hand-edited cockpit.json can.
        var gestures = new List<string> { "Shift+Ctrl+M", "Ctrl+Shift+M" };

        Assert.Equal([0], ShortcutGestureOwnership.DisplacedBy(gestures, claimantIndex: 1));
    }

    [Fact]
    public void AGestureNobodyElseHolds_DisplacesNobody() =>
        Assert.Empty(ShortcutGestureOwnership.DisplacedBy(["Ctrl+Shift+M", "Ctrl+N"], claimantIndex: 1));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("nonsense")]
    public void AnUnboundOrHalfTypedGesture_DisplacesNobody(string claimed)
    {
        // Clearing a shortcut must not clear every other unbound row with it, and a value mid-edit owns nothing.
        var gestures = new List<string> { "", "Ctrl+", claimed };

        Assert.Empty(ShortcutGestureOwnership.DisplacedBy(gestures, claimantIndex: 2));
    }

    [Fact]
    public void MoreThanOneRowHoldingIt_AllGiveItUp()
    {
        // A cockpit.json that already carries duplicates converges the first time one of them is edited, rather
        // than leaving a second silent loser behind.
        var gestures = new List<string> { "Ctrl+M", "Ctrl+M", "Ctrl+N", "Ctrl+M" };

        Assert.Equal([0, 1], ShortcutGestureOwnership.DisplacedBy(gestures, claimantIndex: 3));
    }

    [Fact]
    public void AnIndexOutsideTheList_IsAnsweredRatherThanThrown() =>
        Assert.Empty(ShortcutGestureOwnership.DisplacedBy(["Ctrl+M"], claimantIndex: 7));
}
