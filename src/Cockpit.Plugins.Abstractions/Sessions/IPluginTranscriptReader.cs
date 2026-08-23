namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Reads a TTY session's live transcript for the host's status (#39) feature, in whatever on-disk shape the
/// provider's CLI writes — the host owns neither the location nor the format, so only the provider knows where
/// and how its CLI records the conversation. A provider offers this only if it records a tailable transcript.
/// </summary>
/// <remarks>
/// The provider resolves the transcript location from the profile's own <c>ConfigJson</c> (the same opaque blob
/// it gets in <see cref="PluginTtyLaunchContext.ConfigJson"/>), keeping the host free of any provider's transcript
/// vocabulary. A TTY-only agent that writes no transcript has no reader, and the host simply offers no
/// status-from-transcript for it.
/// </remarks>
public interface IPluginTranscriptReader
{
    /// <summary>
    /// Snapshots the transcript artifacts that already exist for <paramref name="configJson"/> at launch, so
    /// a later tail can single out the one new artifact this session produces. Call once, before the session
    /// is spawned. The returned set is opaque to the host — it only hands it back to the reader unchanged.
    /// </summary>
    IReadOnlySet<string> SnapshotTranscripts(string configJson);

    /// <summary>
    /// Waits for a transcript to appear for <paramref name="configJson"/> that was not in
    /// <paramref name="knownTranscriptsAtLaunch"/> (this session's own new artifact), then classifies each
    /// appended line into a coarse <see cref="PluginSessionActivity"/> for the host's TTY status dot. Tails from
    /// the artifact's current end — never the session's prior history — until <paramref name="cancellationToken"/>
    /// is cancelled.
    /// </summary>
    /// <remarks>
    /// Also carries the raw line (<see cref="PluginTranscriptActivity.RawLine"/>) so the host's output-signal
    /// observe surface reads from the same tail. A provider that records background work apart from the main
    /// transcript (a sub-agent) emits <see cref="PluginSessionActivity.BackgroundBusy"/> as a keep-alive while
    /// that work runs, so a long background run never reads as done.
    /// </remarks>
    IAsyncEnumerable<PluginTranscriptActivity> ReadActivityAsync(string configJson, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken);

    /// <summary>
    /// The same tail, told <em>which session</em> it is reading for (AC-609): <paramref name="statusFile"/> is the
    /// snapshot file this session's own statusline writes, or null when this provider installs no such relay. A
    /// provider that can name its own transcript should do so here rather than let the host guess from what's new
    /// on disk.
    /// </summary>
    /// <remarks>
    /// Guessing from what appeared after launch risks latching onto a foreign artifact from another invocation of
    /// the same CLI on the machine — if that process then exits, the session emits no activity ever, and the pane
    /// sits stuck at its pre-launch status. The default implementation forwards to the overload above, so a
    /// provider with no such file — or an already-compiled plugin — keeps the old behaviour unchanged.
    /// </remarks>
    IAsyncEnumerable<PluginTranscriptActivity> ReadActivityAsync(
        string configJson, IReadOnlySet<string> knownTranscriptsAtLaunch, string? statusFile, CancellationToken cancellationToken) =>
        ReadActivityAsync(configJson, knownTranscriptsAtLaunch, cancellationToken);

    /// <summary>
    /// Reads back the last <paramref name="count"/> rows this session has already written (AC-609), for read
    /// surfaces that ask what a session <em>did</em> rather than what it is doing — the assistant's
    /// <c>read_transcript</c>. Keyed on the same <paramref name="statusFile"/> as the tail above, since without it
    /// there is no honest way to say which artifact belongs to this session.
    /// </summary>
    /// <remarks>
    /// Returns the last rows oldest-first, alongside the total the transcript holds, so a caller handed the tail
    /// of a long session can tell a slice from the whole conversation. Fewer than asked (or none) when the session
    /// has written less, has written nothing yet, or its transcript cannot be read — it must not throw, since an
    /// empty answer is the honest one for a file caught mid-write. The default reports no entries, for a provider
    /// with no status snapshot or an already-compiled plugin.
    /// </remarks>
    PluginTranscriptSlice ReadEntries(string? statusFile, int count) => PluginTranscriptSlice.Empty;
}
