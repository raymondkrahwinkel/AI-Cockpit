using Avalonia.Input;

namespace Cockpit.App.ViewModels;

// One gesture, one owner (AC-608). Deciding which rows have to give theirs up when another row takes it.
// Nothing used to enforce this, and the dispatch does not either: `CockpitView._TryHandleShortcut` walks
// `ActiveShortcuts` in catalog order and invokes the *first* gesture that matches. Bind a chord that
// another action already holds and the winner is whichever sits earlier in the list — so the binding the operator
// just made silently never fires, or the one they forgot about silently stops. Ctrl+Shift+M is the case that was
// reported, because it is Toggle zoom's default and every row after it in the catalog loses to it.
//
// Gestures are compared parsed rather than as text: "Ctrl+Shift+M" and "Shift+Ctrl+M" are one gesture written two
// ways, and a hand-edited `cockpit.json` can hold either. A string comparison would let the second one back
// in through exactly the door this closes.
public static class ShortcutGestureOwnership
{
    // The positions in `gestures` that must be cleared because the row at
    // `claimantIndex` now holds their gesture. Empty when the claimant is unbound or its gesture
    // is unparseable — a half-typed value displaces nobody.
    public static IReadOnlyList<int> DisplacedBy(IReadOnlyList<string> gestures, int claimantIndex)
    {
        if (claimantIndex < 0 || claimantIndex >= gestures.Count || _Parse(gestures[claimantIndex]) is not { } claimed)
        {
            return [];
        }

        var displaced = new List<int>();
        for (var index = 0; index < gestures.Count; index++)
        {
            if (index != claimantIndex && _Parse(gestures[index]) is { } held && held.Equals(claimed))
            {
                displaced.Add(index);
            }
        }

        return displaced;
    }

    // KeyGesture.Parse throws on a blank or half-typed string; an unreadable gesture owns nothing and loses nothing.
    private static KeyGesture? _Parse(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return null;
        }

        try
        {
            return KeyGesture.Parse(gesture);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
