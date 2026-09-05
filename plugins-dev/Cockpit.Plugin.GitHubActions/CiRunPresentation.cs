using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Material.Icons;

namespace Cockpit.Plugin.GitHubActions;

// The at-a-glance appearance of a workflow run's state (AC-52/AC-1065), shared by the session header's single dot
// and the dock panel's list so both read the same icon and colour for the same state.
internal static class CiRunPresentation
{
    public static (MaterialIconKind Kind, IBrush Brush) Appearance(CiRunState state) => state switch
    {
        CiRunState.Passed => (MaterialIconKind.CheckCircleOutline, _Brush("CockpitStatusDoneBrush", "#5AA576")),
        CiRunState.Failed => (MaterialIconKind.CloseCircleOutline, _Brush("CockpitStatusErrorBrush", "#D64545")),
        CiRunState.Running => (MaterialIconKind.ProgressClock, _Brush("CockpitStatusWaitingBrush", "#E0A33E")),
        // Fallback only fires with no Application (designer/headless) — a plugin always runs inside the host, so
        // this never changes what a user sees.
        _ => (MaterialIconKind.MinusCircleOutline, _Brush("CockpitTextFaintBrush", "#656c78")),
    };

    // "just now" / "3m ago" / "2h ago" / "1d ago" for a past timestamp.
    public static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

    // "45s" / "3m12s" / "1h05m" for a run's Duration. Null (still running, or an older run missing updatedAt)
    // renders as an em dash rather than a misleading "0s".
    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } span)
        {
            return "—";
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
                : $"{span.Seconds}s";
    }

    // The host's theme brush, resolved at call time so a repaint of the token is followed. The fallback hex fires
    // only with no `Application` and is held equal to the theme's token by the repository's theme guard.
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
