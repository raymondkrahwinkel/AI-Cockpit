namespace Cockpit.Core.Sessions;

/// <summary>
/// A session's live status (#45 D7): how full its context window is and the usage windows its provider reports.
/// The core mirror of the plugin surface's <c>PluginSessionStatus</c> — a provider-neutral feed the session
/// header renders its bars from, whichever provider filled it. The windows are a self-labelled list, so the
/// header holds no five-hour/weekly vocabulary of its own.
/// </summary>
/// <param name="ContextUsedPercent">How full the context window is, 0-100, or <see langword="null"/> before the provider reports it.</param>
/// <param name="RateLimits">The usage windows the provider reports, each self-labelled; empty when it reports none.</param>
public sealed record SessionStatusFeed(
    double? ContextUsedPercent,
    IReadOnlyList<SessionRateWindow> RateLimits)
{
    /// <summary>Whether there is anything worth showing, so the header hides the bars until a provider reports usage.</summary>
    public bool HasAny => ContextUsedPercent is not null || RateLimits.Count > 0;

    /// <summary>
    /// The hover text for the header's bars: what each bar means, spelled out, plus when each window rolls over —
    /// the one thing a bar cannot show and the thing you want when it is nearly full. Only the numbers the
    /// provider reported, so nothing here is invented.
    /// </summary>
    public string Describe()
    {
        var lines = new List<string>(RateLimits.Count + 1);

        if (ContextUsedPercent is { } context)
        {
            lines.Add($"Context window: {Rounded(context)}% used");
        }

        foreach (var window in RateLimits)
        {
            lines.Add($"{window.Label}: {Rounded(window.UsedPercent)}% used{Resets(window.ResetsAt)}");
        }

        return string.Join(Environment.NewLine, lines);

        static string Resets(DateTimeOffset? resetsAt) =>
            resetsAt is { } at ? $" — resets {at.ToLocalTime():ddd HH:mm}" : string.Empty;

        // Away from zero, not .NET's default banker's rounding — which turns 42.5% into 42% and would have the
        // header quietly under-report exactly on the halves.
        static double Rounded(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
