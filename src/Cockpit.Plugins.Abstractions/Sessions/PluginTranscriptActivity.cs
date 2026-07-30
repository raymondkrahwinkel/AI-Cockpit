namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One activity reading from a provider's transcript: the classified <see cref="Activity"/> plus the
/// <see cref="RawLine"/> it came from (null for a synthetic keep-alive that no single line produced), so the host
/// can drive both the status dot and a raw-line observe surface (output-signal scanning) from one tail.
/// </summary>
/// <param name="Activity">The classified turn-activity this reading represents.</param>
/// <param name="RawLine">The raw transcript line, or null for a synthetic signal (e.g. a background keep-alive).</param>
/// <param name="Usage">
/// The token usage this transcript line carried (AC-398), or null when the line carried none — most lines. A
/// provider that records no usage in its transcript at all simply never sets this, which reads to the host
/// exactly like a session with nothing to report yet.
/// </param>
/// <param name="OutstandingShells">
/// How many backgrounded shells this session still has running (AC-276), when the provider can tell. Separate from
/// <see cref="PluginSessionActivity.BackgroundBusy"/> on purpose: a shell may be a dev server or a <c>tail -f</c>
/// that never ends, so it must not hold the status the way a sub-agent does — the host uses this only to withhold
/// the "session finished" notification. Zero (the default) reads as "none outstanding", which is what a provider
/// with no such notion reports by never setting it.
/// </param>
public sealed record PluginTranscriptActivity(
    PluginSessionActivity Activity,
    string? RawLine,
    PluginTokenUsage? Usage = null,
    int OutstandingShells = 0);
