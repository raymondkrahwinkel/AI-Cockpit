namespace Cockpit.Core.Abstractions.Screenshots;

// AC-1013 (AC-360): fractional-pixel corner type, distinct from whole-pixel `CapturePoint`. Deleted: the
// worked example showing rounded arrow barbs come out visibly lopsided since each rounds a different way.
public readonly record struct MarkPoint(double X, double Y);
