namespace Cockpit.Core.Abstractions.Screenshots;

// AC-359: something the operator put on the capture, held as an ordered list and burnt into the pixels on
// confirm — never carried alongside the image, generalising AC-331's rule that a redaction a path forgets to
// composite must not silently leak. Geometry belongs to each kind; `ClipTo` is the shared cropping contract.
public abstract record Mark
{
    // AC-1013: mark as it sits in a cropped image (region in the capture's pixels), or null if entirely
    // outside it — clipped rather than dropped whenever any part survives, since a box half off-edge was meant.
    public abstract Mark? ClipTo(CaptureRect region);

    // AC-1013 (AC-360/AC-362): contrast ring colour for a drawn (unfilled) mark, since a screenshot has no
    // single background. Weighted per human eye sensitivity, not averaged — deleted: example of a saturated
    // green/blue pair with equal arithmetic mean but very unequal brightness that averaging would get wrong.
    protected static uint ContrastingWith(uint colour) =>
        ((0.299 * ((colour >> 16) & 0xFF)) + (0.587 * ((colour >> 8) & 0xFF)) + (0.114 * (colour & 0xFF))) < 128
            ? 0xFFFFFFFF
            : 0xFF000000;
}
