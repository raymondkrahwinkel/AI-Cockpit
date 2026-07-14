using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// A hairline that moves while something is being fetched, sitting directly above the list it is filling.
/// <para>
/// The plugins already wrote "Loading…" into a status line, which is a thing you only see if you were already
/// looking for it — and shelling out to <c>gh</c> across several repositories takes long enough that a list
/// which simply sits there reads as an empty list rather than a busy one. Two pixels of movement is the
/// difference between "there is nothing" and "there is nothing yet".
/// </para>
/// <para>
/// Deliberately not a spinner in the middle of the grid: the previous results stay readable and in place while
/// a refresh runs, so a refresh never costs the operator the thing they were reading.
/// </para>
/// </summary>
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
        Foreground = Brush("CockpitAccentBrush", Brushes.Coral),
        Background = Brushes.Transparent,
        BorderThickness = default,
    };

    private static IBrush Brush(string key, IBrush fallback) =>
        Avalonia.Application.Current?.TryFindResource(key, out var resource) == true && resource is IBrush brush
            ? brush
            : fallback;
}
