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

    /// <summary>
    /// The same tail, told <em>which session</em> it is reading for (AC-609): <paramref name="statusFile"/> is the
    /// snapshot file this session's own statusline writes — the one the host already polls for usage readings
    /// (<c>TtyProviderRegistration.ReadUsage</c>) — or null when this provider installs no such relay.
    /// <para>
    /// It exists because "the session's own transcript" cannot be guessed. The only signal the overload above has
    /// is which artifacts were on disk before the launch, so it must take the new one that appears afterwards —
    /// and every other invocation of the same CLI anywhere on the machine (another pane, an SDK session, a
    /// delegated task, the operator's own terminal) produces one of those too. Losing that race is not a near
    /// miss: the reader latches onto a foreign artifact, tails it to its end, and if that process then exits the
    /// session emits no activity <em>ever</em> — the host has no signal to time out on, so the pane sits at its
    /// pre-launch status for its whole life while working normally. A provider that can name its own transcript
    /// (the CLI states it in the status snapshot) should do so here and never guess.
    /// </para>
    /// A default implementation forwards to the overload above, so a provider that has no such file — and an
    /// already-compiled plugin that never saw this method — keeps the old behaviour unchanged.
    /// </summary>
    IAsyncEnumerable<PluginTranscriptActivity> ReadActivityAsync(
        string configJson, IReadOnlySet<string> knownTranscriptsAtLaunch, string? statusFile, CancellationToken cancellationToken) =>
        ReadActivityAsync(configJson, knownTranscriptsAtLaunch, cancellationToken);

    /// <summary>
    /// Reads back the last <paramref name="count"/> rows this session has already written (AC-609), for the read
    /// surfaces that ask what a session <em>did</em> rather than what it is doing — the assistant's
    /// <c>read_transcript</c>. A TTY session hosts the provider's real TUI, so unlike an SDK session the host holds
    /// no transcript of its own and this file is the only record there is.
    /// <para>
    /// Keyed on the same <paramref name="statusFile"/> as the tail above, and for the same reason: without it there
    /// is no honest way to say which artifact belongs to this session, and answering with somebody else's
    /// conversation is far worse than answering with nothing. The default implementation therefore reports no
    /// entries, which is what a provider with no status snapshot — or an already-compiled plugin — reports.
    /// </para>
    /// Returns the last rows oldest-first, alongside the total the transcript holds — a caller handed the tail of a
    /// long session has to be able to say so rather than reporting a slice as the whole conversation. Fewer than
    /// asked (or none) when the session has written less than that, has written nothing yet, or its transcript
    /// cannot be read. It must not throw: a file caught mid-write is ordinary, and an empty answer is the honest one.
    /// </summary>
    PluginTranscriptSlice ReadEntries(string? statusFile, int count) => PluginTranscriptSlice.Empty;
}
