namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Reads a TTY session's live transcript for the host's status (#39) feature, in whatever on-disk shape the
/// provider's CLI writes. The host owns neither the location nor the format — a TTY session runs the real
/// interactive TUI, so there is no parsed event stream, and only the provider knows where and how its CLI
/// records the conversation. The provider resolves that from the profile's own <c>ConfigJson</c> (the same
/// opaque blob it gets in <see cref="PluginTtyLaunchContext.ConfigJson"/>), so the host stays free of any
/// provider's transcript vocabulary.
/// <para>
/// A provider offers this only if it records a tailable transcript; a TTY-only agent that writes nothing has
/// no reader, and the host simply offers no status-from-transcript for it.
/// </para>
/// </summary>
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
    /// appended line into a coarse <see cref="PluginSessionActivity"/> for the host's TTY status dot — the
    /// provider owns the format-specific reading, so the host maps neutral signals rather than parsing a
    /// transcript. Also carries the raw line (<see cref="PluginTranscriptActivity.RawLine"/>) so the host's
    /// output-signal observe surface reads from the same tail. A provider that runs background work (a
    /// sub-agent) it records apart from the main transcript emits <see cref="PluginSessionActivity.BackgroundBusy"/>
    /// as a keep-alive while that work runs, so a long background run never reads as done. Tails from the
    /// artifact's current end, so only lines written after this call are seen — never the session's prior
    /// history. Runs until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<PluginTranscriptActivity> ReadActivityAsync(string configJson, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken);
}
