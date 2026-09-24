using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The assistant's own session as <c>AssistantSessionHost</c> drives it (AC-1379): the desktop pane on the desktop,
/// a headless session in the backend. Each implementation marshals to its own thread; the host calls it on one.
/// </summary>
public interface IAssistantSession : IAsyncDisposable
{
    /// <summary>
    /// Whether its runtime is running — asked of the session, since a runtime can end without telling anyone.
    /// </summary>
    bool IsSessionReady { get; }

    /// <summary>
    /// Whether a turn is in flight.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Whether a permission row or a consent card waits on the operator.
    /// </summary>
    bool IsWaitingOnOperator { get; }

    /// <summary>
    /// How full the context is, as the provider last reported it; null when it reported nothing.
    /// </summary>
    double? ContextUsedPercent { get; }

    /// <summary>
    /// Whether the provider can summarise its own conversation (AC-664).
    /// </summary>
    bool SupportsContextCompaction { get; }

    /// <summary>
    /// A running runtime and no hold on new turns.
    /// </summary>
    bool CanTakeAPrompt { get; }

    /// <summary>
    /// Why new turns are held (AC-1321), or null when they are not.
    /// </summary>
    string? TurnsHeldBecause { get; set; }

    /// <summary>
    /// The reading level its rows are shown at (AC-138).
    /// </summary>
    ReadingLevel ReadingLevel { get; set; }

    /// <summary>
    /// Why a start did not leave it running, for the operator.
    /// </summary>
    string FailureReason { get; }

    /// <summary>
    /// Whether the provider can see images (AC-1049).
    /// </summary>
    bool CanPasteImages { get; }

    /// <summary>
    /// Whether attachments wait to ride on the next message.
    /// </summary>
    bool HasPendingAttachments { get; }

    /// <summary>
    /// Busy, waiting on the operator or the context fill moved; true when a new fill figure was just read.
    /// </summary>
    event EventHandler<bool>? StateChanged;

    /// <summary>
    /// A transcript row formed or changed, from the one writer its rows have (AC-1377).
    /// </summary>
    event Action<TranscriptRowUpsert>? RowUpserted;

    /// <summary>
    /// The top-level rows as they stand now — what a chat channel joining mid-conversation takes as already said.
    /// </summary>
    IReadOnlyList<TranscriptSnapshotEntry> Rows { get; }

    /// <summary>
    /// Replays the recorded conversation when <paramref name="resume"/> continues it, else rolls the log (AC-1080).
    /// </summary>
    Task PrepareRecordedTranscriptAsync(SessionResume resume, CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches its runtime; a failure shows in <see cref="IsSessionReady"/> and <see cref="FailureReason"/>, never as a throw.
    /// </summary>
    Task StartAsync(AssistantLaunch launch);

    /// <summary>
    /// Attaches one PNG to the next message.
    /// </summary>
    void AddPastedImage(byte[] pngBytes);

    /// <summary>
    /// Sends what the composer holds, attachments included.
    /// </summary>
    void SubmitComposer();

    /// <summary>
    /// Sends <paramref name="text"/> as the operator's message.
    /// </summary>
    void InjectAndSubmit(string text);

    /// <summary>
    /// Asks the provider to compact the conversation; false when it could not be asked.
    /// </summary>
    Task<bool> CompactContextAsync();

    /// <summary>
    /// Adds a divider row saying why the conversation changed course (AC-638).
    /// </summary>
    void AddDivider(string text);

    /// <summary>
    /// Answers an open consent card No, so its tool call does not hang on a session being torn down.
    /// </summary>
    void DenyPendingConsent();

    /// <summary>
    /// Seeds its speech from the operator's settings; a session with no voice ignores it.
    /// </summary>
    void ApplySpeech(bool speakReplies);

    /// <summary>
    /// Turns reading replies aloud on or off from the next reply on.
    /// </summary>
    void SpeakReplies(bool speak);
}

// AC-1379: one assistant launch. The model, effort and permission-mode floor are each implementation's own defaults;
// the profile's option defaults ride LaunchOptions over them.
public sealed record AssistantLaunch(
    SessionProfile Profile,
    string WorkingDirectory,
    SessionResume Resume,
    IReadOnlySet<string> EnabledMcpServerNames,
    IReadOnlyDictionary<string, string> LaunchOptions,
    ReadingLevel ReadingLevel);
