namespace Cockpit.Plugin.UsageTrend;

/// <summary>
/// The pure history rules for the usage-trend widget (AC-54), kept apart from the widget and its storage so they
/// can be tested on a list rather than a live session: what to keep, what to throw away, and — the two decisions
/// that keep this out of <c>cockpit.json</c>'s way — when a fresh reading is worth writing and when it is just the
/// same story a moment later.
/// <list type="bullet">
/// <item><description>
/// <b>Debounce.</b> The session header updates every few seconds; writing each tick would rewrite the whole
/// settings file hundreds of times an hour. So a reading is written at most once per <see cref="MinInterval"/> per
/// profile — unless it jumped.
/// </description></item>
/// <item><description>
/// <b>Jump.</b> A sharp move — a <c>/compact</c> dropping the context, an allowance climbing past
/// <see cref="JumpThresholdPercent"/> points — is written straight away, so a knife-edge is not flattened into the
/// gap between two debounced points.
/// </description></item>
/// <item><description>
/// <b>Retention.</b> Nothing older than <see cref="RetentionDays"/> is kept — a trend widget answers "how did my
/// week go", not "my year", and that ceiling is what keeps the payload in <c>cockpit.json</c> small.
/// </description></item>
/// </list>
/// All per profile: debounce and jump compare against the last sample for the <em>same</em> profile label, so one
/// busy profile does not gate another's first point.
/// </summary>
internal static class UsageTrendHistory
{
    /// <summary>How long history is kept — 14 days, the AC-54 retention (§3): enough for "how did my week go", small enough to stay tens of KB in the settings file.</summary>
    public const int RetentionDays = 14;

    /// <summary>The most a routine reading is written: once per 10 minutes per profile. A jump overrides it.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    /// <summary>A move of at least this many percentage points on any metric is written at once, debounce or not — a scarp the 10-minute grid would otherwise skip.</summary>
    public const double JumpThresholdPercent = 10.0;

    /// <summary>
    /// Decides what the history becomes when <paramref name="candidate"/> is offered, and returns the new list —
    /// or <see langword="null"/> when nothing changes, so the caller can skip the write entirely (the debounce that
    /// keeps <c>cockpit.json</c> quiet). A candidate with no usage figure at all is never recorded.
    /// </summary>
    public static IReadOnlyList<UsageTrendSample>? Append(IReadOnlyList<UsageTrendSample> existing, UsageTrendSample candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!candidate.HasAny)
        {
            return null;
        }

        var last = _LastForProfile(existing, candidate.ProfileLabel);
        if (!_ShouldRecord(last, candidate))
        {
            return null;
        }

        // Prune against the candidate's own clock so a long-idle history that only now gets a point still sheds its
        // stale tail in the same write.
        var kept = Prune([.. existing, candidate], candidate.TimestampUtc);
        return kept;
    }

    /// <summary>Drops every sample older than <see cref="RetentionDays"/> before <paramref name="now"/>, keeping the rest in order. Public for the widget's load path, which prunes what it read before charting it.</summary>
    public static IReadOnlyList<UsageTrendSample> Prune(IEnumerable<UsageTrendSample> samples, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var cutoff = now - TimeSpan.FromDays(RetentionDays);
        return
        [
            .. samples
                .Where(sample => sample.TimestampUtc >= cutoff)
                .OrderBy(sample => sample.TimestampUtc),
        ];
    }

    /// <summary>Whether <paramref name="candidate"/> is worth writing given the last sample for its profile: the first one always, one past the debounce window always, and one that jumped even inside the window.</summary>
    private static bool _ShouldRecord(UsageTrendSample? last, UsageTrendSample candidate)
    {
        if (last is null)
        {
            return true;
        }

        if (candidate.TimestampUtc - last.TimestampUtc >= MinInterval)
        {
            return true;
        }

        return _Jumped(last.ContextPercent, candidate.ContextPercent)
            || _Jumped(last.FiveHourPercent, candidate.FiveHourPercent)
            || _Jumped(last.WeeklyPercent, candidate.WeeklyPercent);
    }

    /// <summary>
    /// Whether a metric moved enough to write out of turn. A value appearing or disappearing (one side null) is a
    /// jump in itself — a context reset after <c>/compact</c> reads as a value going to null, and a subscription's
    /// first rate limit as null becoming a value, both worth a point. Two nulls is no movement.
    /// </summary>
    private static bool _Jumped(double? previous, double? current)
    {
        if (previous is null && current is null)
        {
            return false;
        }

        if (previous is null || current is null)
        {
            return true;
        }

        return Math.Abs(current.Value - previous.Value) >= JumpThresholdPercent;
    }

    private static UsageTrendSample? _LastForProfile(IReadOnlyList<UsageTrendSample> existing, string? profileLabel)
    {
        UsageTrendSample? last = null;
        foreach (var sample in existing)
        {
            if (string.Equals(sample.ProfileLabel, profileLabel, StringComparison.Ordinal)
                && (last is null || sample.TimestampUtc >= last.TimestampUtc))
            {
                last = sample;
            }
        }

        return last;
    }
}
