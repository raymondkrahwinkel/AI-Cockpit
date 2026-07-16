namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One activity reading from a provider's transcript: the classified <see cref="Activity"/> plus the
/// <see cref="RawLine"/> it came from (null for a synthetic keep-alive that no single line produced), so the host
/// can drive both the status dot and a raw-line observe surface (output-signal scanning) from one tail.
/// </summary>
/// <param name="Activity">The classified turn-activity this reading represents.</param>
/// <param name="RawLine">The raw transcript line, or null for a synthetic signal (e.g. a background keep-alive).</param>
public sealed record PluginTranscriptActivity(PluginSessionActivity Activity, string? RawLine);
