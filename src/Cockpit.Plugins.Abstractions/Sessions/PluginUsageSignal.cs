namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One thing a provider's sessions can run out of, declared so the host can warn about it and act on it without
/// knowing what it is (AC-229). A provider that reports a context window and two rolling caps declares three of
/// these; one with a per-minute quota declares that instead; one that measures nothing declares none, and then no
/// pill, no warning and no setting appears for it.
/// <para>
/// The provider owns the numbers as well as the vocabulary. It knows what its window means and therefore when it
/// is worth interrupting for, so the threshold ships with the declaration rather than being a constant the host
/// picked for everybody.
/// </para>
/// </summary>
/// <param name="Key">How a reading names this signal, and how a stored threshold finds it again. Stable — a rename orphans the operator's setting.</param>
/// <param name="Label">The short text the header shows, chosen by the provider (e.g. "ctx", "5h", "wk").</param>
/// <param name="Kind">Whether this fills and drains or spends down and rolls over; see <see cref="PluginUsageSignalKind"/>.</param>
/// <param name="DefaultThresholdPercent">
/// How full it has to be before the cockpit says something, 0-100. The provider's answer; an operator can
/// override it per provider, and a profile can override that again.
/// </param>
public sealed record PluginUsageSignal(
    string Key,
    string Label,
    PluginUsageSignalKind Kind,
    double DefaultThresholdPercent)
{
    /// <summary>
    /// The signal spelled out for the hover text, where there is room for words — "Context window" behind a pill
    /// that reads "ctx". Falls back to <see cref="Label"/> when the provider offers none, so a short label is
    /// still a complete declaration.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether a session that has spent this allowance can be offered a resume timed to its reset
    /// (AC-231). Only meaningful for <see cref="PluginUsageSignalKind.Allowance"/> — a fill has no moment to
    /// schedule against. <see langword="false"/> by default, so a provider offers nothing it has not thought about.
    /// </summary>
    public bool SupportsResume { get; init; }

    /// <summary>
    /// The prompt a scheduled resume sends unless the operator edits it — "continue" for a CLI that understands
    /// it, something else for one that does not. <see langword="null"/> when the provider has no sensible default,
    /// and the operator then writes their own before scheduling.
    /// </summary>
    public string? DefaultResumePrompt { get; init; }
}
