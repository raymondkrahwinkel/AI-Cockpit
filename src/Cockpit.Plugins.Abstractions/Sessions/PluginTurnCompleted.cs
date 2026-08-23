namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A turn finished.
/// </summary>
public sealed record PluginTurnCompleted : PluginSessionEvent
{
    public required string Subtype { get; init; }

    public required string? Result { get; init; }

    public required bool IsError { get; init; }

    public string? StopReason { get; init; }

    /// <summary>
    /// Token counts this turn cost (#45 D3), when the provider reports them — folded into the host's running
    /// token/cost meter (#8). Optional: a provider that reports no usage leaves it <see langword="null"/>, and an
    /// already-compiled plugin that never sets it keeps constructing this the old way.
    /// </summary>
    public PluginTokenUsage? Usage { get; init; }

    /// <summary>
    /// What the session has cost in USD so far — a running total, not this turn's share — when the provider reports one; <see langword="null"/> when it does not (most do not — they have no pricing).
    /// </summary>
    public double? TotalCostUsd { get; init; }

    /// <summary>
    /// The provider's own turn count for the session, when it reports one.
    /// </summary>
    public int? NumTurns { get; init; }

    /// <summary>
    /// Why the turn failed, in the provider's own words, when <see cref="Subtype"/> is an error and
    /// <see cref="Result"/> was never set to explain it (AC-410) — e.g. Claude's
    /// <c>error_during_execution</c> for an unresolvable <c>--resume</c> id reports
    /// <c>["No conversation found with session ID: …"]</c> here and nothing in <see cref="Result"/>. Null when the
    /// provider reported none, which is every turn that is not this failure mode. Optional like <see cref="Usage"/>
    /// above: an already-compiled plugin that never sets it keeps constructing this the old way.
    /// </summary>
    public IReadOnlyList<string>? Errors { get; init; }
}
