namespace Cockpit.Core.Abstractions.Screenshots;

// A corner of a mark's shape, in fractions of a pixel (AC-360). Its own type rather than
// `CapturePoint` because that one counts whole pixels, which is right for the things an operator
// puts down — a drag begins and ends on a pixel — and wrong for the corners worked out from them.
// An arrow drawn at an angle has its barbs at distances that are not whole numbers. Rounding each to a pixel
// bends the shape by up to a pixel per corner, and the two barbs round different ways depending on the angle, so
// the head comes out lopsided at some directions and square at others. That is visible at a glance and there is
// nothing to gain from it: the imaging library and the surface both draw in floating point anyway.
public readonly record struct MarkPoint(double X, double Y);
