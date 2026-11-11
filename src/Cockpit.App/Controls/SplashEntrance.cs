using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace Cockpit.App.Controls;

// AC-2318: the Wispslate splash entrance, modelled on the Synvolution One splash (AC-2179). Everything runs on the
// compositor (Offset, Scale, Opacity), so it keeps moving while the UI thread builds the main window (AC-1829).
// Each track is the mockup's own curve sampled into linear keyframes; the XAML holds the end state, which reduced motion keeps.
public static class SplashEntrance
{
    private const int Samples = 40;

    // Windows "animation effects" off. Avalonia 12.1 has no platform API for it.
    public static bool IsReducedMotion { get; } = _ReadReducedMotion();

    public static void Run(Control full, Control trail, Control body, Control name, Control sheenBand)
    {
        if (ElementComposition.GetElementVisual(full) is not { } fullVisual
            || ElementComposition.GetElementVisual(trail) is not { } trailVisual
            || ElementComposition.GetElementVisual(body) is not { } bodyVisual
            || ElementComposition.GetElementVisual(name) is not { } nameVisual
            || ElementComposition.GetElementVisual(sheenBand) is not { } bandVisual)
        {
            return;
        }

        var compositor = fullVisual.Compositor;
        var bodyBase = bodyVisual.Offset;
        var trailBase = trailVisual.Offset;
        var nameBase = nameVisual.Offset;
        var bandBase = bandVisual.Offset;

        bodyVisual.CenterPoint = new Vector3D(body.Bounds.Width / 2, body.Bounds.Height / 2, 0);
        trailVisual.CenterPoint = new Vector3D(trail.Bounds.Width * 0.33, trail.Bounds.Height / 2, 0);

        // Plate and W arc in from the lower left: x eases slower than y, so the path bends.
        Move(bodyVisual, "Offset", 1300, t =>
        {
            var k = _Seg(t, 0, 900);
            return new Vector3D(bodyBase.X + _Lerp(-46, 0, _EaseOut(k)), bodyBase.Y + _Lerp(16, 0, _EaseOut(_Clamp(k * 1.6))), 0);
        });
        Move(bodyVisual, "Scale", 1300, t => { var s = _Lerp(.94, 1, _EaseOut(_Seg(t, 0, 900))); return new Vector3D(s, s, 1); });
        Fade(bodyVisual, "Opacity", 1300, t => (float)_EaseOut(_Seg(t, 0, 900)));

        // The streaks follow 120 ms later, stretched, and tighten in behind the W.
        Move(trailVisual, "Offset", 1300, t =>
        {
            var k = _Wisp(_Seg(t, 120, 1300));
            return new Vector3D(trailBase.X + _Lerp(-96, 0, k), trailBase.Y + _Lerp(16, 0, _EaseOut(_Clamp(_Seg(t, 120, 1020) * 1.6))), 0);
        });
        Move(trailVisual, "Scale", 1300, t => new Vector3D(_Lerp(1.7, 1, _Wisp(_Seg(t, 120, 1300))), 1, 1));
        Fade(trailVisual, "Opacity", 1300, t => (float)_EaseOut(_Seg(t, 120, 620)));

        // The two halves give way to the whole image, so the end frame has no seam.
        Fade(fullVisual, "Opacity", 1300, t => t >= 1300 ? 1f : 0f);

        Fade(nameVisual, "Opacity", 1300, t => (float)_EaseOut(_Seg(t, 750, 1300)));
        Move(nameVisual, "Offset", 1300, t => new Vector3D(nameBase.X, nameBase.Y + _Lerp(6, 0, _Wisp(_Seg(t, 750, 1300))), 0));

        // One wisp gradient runs once through the mark, in the direction the streaks point.
        Move(bandVisual, "Offset", 2600, t => new Vector3D(bandBase.X + _Lerp(0, 296, _EaseInOut(_Seg(t, 1500, 2600))), bandBase.Y, 0));

        void Move(CompositionVisual visual, string property, int ms, Func<double, Vector3D> value)
        {
            var animation = compositor.CreateVector3DKeyFrameAnimation();
            animation.Target = property;
            animation.Duration = TimeSpan.FromMilliseconds(ms);
            for (var i = 0; i <= Samples; i++)
            {
                animation.InsertKeyFrame((float)i / Samples, value((double)i / Samples * ms), new LinearEasing());
            }

            visual.StartAnimation(property, animation);
        }

        void Fade(CompositionVisual visual, string property, int ms, Func<double, float> value)
        {
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Target = property;
            animation.Duration = TimeSpan.FromMilliseconds(ms);
            for (var i = 0; i <= Samples; i++)
            {
                animation.InsertKeyFrame((float)i / Samples, value((double)i / Samples * ms), new LinearEasing());
            }

            visual.StartAnimation(property, animation);
        }
    }

    private static double _Clamp(double x) => Math.Min(1, Math.Max(0, x));

    private static double _Seg(double t, double from, double to) => _Clamp((t - from) / (to - from));

    private static double _Lerp(double a, double b, double k) => a + (b - a) * k;

    private static double _EaseOut(double x) => 1 - Math.Pow(1 - x, 3);

    private static double _EaseInOut(double x) => x < .5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    // The Synvolution transition easing, cubic-bezier(.22, 1, .36, 1).
    private static double _Wisp(double x)
    {
        static double Bezier(double t, double p, double q) => 3 * p * (1 - t) * (1 - t) * t + 3 * q * (1 - t) * t * t + t * t * t;
        double lo = 0, hi = 1;
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2;
            if (Bezier(mid, .22, .36) < x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return Bezier((lo + hi) / 2, 1, 1);
    }

    private static bool _ReadReducedMotion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return SystemParametersInfo(0x1042, 0, out var enabled, 0) && enabled == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}
