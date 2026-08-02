using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.Plugin.YouTrack;

// A hairline that moves while something is being fetched, sitting directly above the list it is filling.
//
// The plugins already wrote "Loading…" into a status line, which is a thing you only see if you were already
// looking for it — and shelling out to `gh` across several repositories takes long enough that a list
// which simply sits there reads as an empty list rather than a busy one. Two pixels of movement is the
// difference between "there is nothing" and "there is nothing yet".
//
// Deliberately not a spinner in the middle of the grid: the previous results stay readable and in place while
// a refresh runs, so a refresh never costs the operator the thing they were reading.
internal static class LoadingBar
{
    public static ProgressBar Build() => new()
    {
        IsIndeterminate = true,
        IsVisible = false,
        Height = 2,
        MinHeight = 2,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
        Foreground = Brush("CockpitAccentBrush", "#2563eb"),
        Background = Brushes.Transparent,
        BorderThickness = default,
    };

    // The host's accent, resolved at call time. The fallback hex is only reached with no
    // `Avalonia.Application` (designer, headless test) and is held equal to its token by the
    // repository's theme guard. It used to be one of the framework's own named colours, a near-enough stand-in
    // while the accent was orange and a wrong one the moment it moved to blue (AC-334).
    private static IBrush Brush(string key, string fallbackHex) =>
        Avalonia.Application.Current?.TryFindResource(key, out var resource) == true && resource is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
