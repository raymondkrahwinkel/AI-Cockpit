namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What one <see cref="PluginUsageSignal"/> stands at right now (AC-229) — the value half of the declaration.
/// </summary>
/// <remarks>
/// The host matches a reading to its declaration by <see cref="SignalKey"/> and renders the result; it never learns
/// what was read or from where.
/// </remarks>
/// <param name="SignalKey">
/// Which declared signal this is a value for. A reading whose key matches no declaration is ignored rather than guessed at.
/// </param>
/// <param name="UsedPercent">
/// How full it is, 0-100.
/// </param>
/// <param name="ResetsAt">
/// When an allowance rolls over, or <see langword="null"/> when this signal has no moment (a fill) or the
/// provider did not say. A resume can only be offered against a reading that carries one.
/// </param>
public sealed record PluginUsageReading(
    string SignalKey,
    double UsedPercent,
    DateTimeOffset? ResetsAt);
