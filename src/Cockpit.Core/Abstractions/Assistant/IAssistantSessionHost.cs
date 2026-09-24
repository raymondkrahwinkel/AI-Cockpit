using System.ComponentModel;
using Cockpit.Core.Assistant;

namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The assistant's owning host as the chat window, the hotkeys, the indicator and a chat channel reach it (AC-543).
/// Its one implementation, <c>AssistantSessionHost</c>, lives in Infrastructure since AC-1379.
/// </summary>
public interface IAssistantSessionHost : INotifyPropertyChanged
{
    /// <summary>
    /// The assistant's own long-running session, or null while it has not been lazily started yet.
    /// </summary>
    IAssistantSession? Session { get; }

    /// <summary>
    /// Turns speaking on or off on the live session, so the header toggle reaches the next reply and not only the
    /// one that is playing. A no-op while nothing has been started yet — the value is read again at the next start.
    /// </summary>
    void SetSpeakReplies(bool speak);

    /// <summary>
    /// What the indicator shows; pairs with <see cref="UnavailableReason"/> when it is <see cref="AssistantActivity.Unavailable"/>.
    /// </summary>
    AssistantActivity Activity { get; }

    /// <summary>
    /// Why the assistant cannot be reached right now (feature off, no profile, failed start) — null otherwise.
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Idempotent lazy start: returns the running session if there is one, restarts it if it fell over, and no-ops (leaving <see cref="Activity"/> at Unavailable) if the feature is off or the slot is empty.
    /// </summary>
    Task<IAssistantSession?> EnsureStartedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stands the running assistant down and brings it back up on the same conversation, so a setting that can
    /// only be chosen at a start actually gets one. Starts it if nothing is running yet.
    /// </summary>
    Task<IAssistantSession?> RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a brand-new conversation right now (AC-1261) — the transcript on screen empties, the old log is
    /// archived (unchanged AC-947 retention), and a divider says why. Never call this while the session is busy;
    /// stop it first.
    /// </summary>
    /// <remarks>
    /// Default-implemented for the same reason as the images overload above: the fakes of this interface that
    /// never clear a conversation stay as they are.
    /// </remarks>
    Task<IAssistantSession?> ClearConversationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IAssistantSession?>(null);

    /// <summary>
    /// Marks a request from the <c>clear_conversation</c> tool to clear once the current turn is no longer busy
    /// and nothing awaits the operator — never immediately, since that would tear down the very session answering
    /// this call. Returns false when a request is already queued (idempotent second call), true otherwise.
    /// </summary>
    /// <remarks>
    /// Default-implemented for the same reason as the images overload above: the fakes of this interface that
    /// never receive this call stay as they are.
    /// </remarks>
    bool RequestConversationClear() => false;

    /// <summary>
    /// Sends typed or spoken text to the assistant, starting it lazily first if it has not run yet.
    /// </summary>
    Task SendAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same with images attached (AC-1049), already validated and PNG-encoded by the caller. They ride on the
    /// message the text sends, or on a message of their own when the text is empty.
    /// </summary>
    /// <remarks>
    /// Default-implemented so the test doubles of this interface that never send an image stay as they are. There
    /// is one real implementation by design (see <c>AssistantSessionHost</c>) and it overrides this.
    /// </remarks>
    Task SendAsync(string text, IReadOnlyList<byte[]> pngImages, CancellationToken cancellationToken = default) =>
        SendAsync(text, cancellationToken);

    /// <summary>
    /// AC-740: the Assistant Profile's own default working directory, once known — read synchronously so the
    /// @-mention picker can fall back to it before any <see cref="Session"/> exists. Null until the profile has
    /// loaded at least once; a read lazily triggers that load, so a window that never opens the picker never pays for it.
    /// </summary>
    string? DefaultWorkingDirectory { get; }

    /// <summary>
    /// Re-reads the settings and stands the assistant down if the feature was switched off — mid-sentence
    /// included, which is the point of it being a separate call rather than something checked on the next use.
    /// </summary>
    Task ApplySettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The assistant hotkey went down or came back up, so the assistant is (or is no longer) the one listening.
    /// Told, not inferred — the indicator used to read "who is listening" off the shared voice pill, so holding
    /// the assistant key lit it up as <em>dictation</em> instead. The coordinator now says so directly.
    /// </summary>
    void ReportHoldListening(bool listening);

    /// <summary>
    /// Speech-to-text is turning the words into text, or has finished doing so. Told for the same reason as
    /// <see cref="ReportHoldListening"/>: the coordinator that ran the hold knows, and the chip must not have to
    /// guess it off a signal that dictation writes to as well.
    /// </summary>
    void ReportTranscribing(bool transcribing);

    /// <summary>
    /// Speech-to-text is fetching what it needs before it can transcribe — <paramref name="status"/> names the
    /// step ("Downloading speech model") and <paramref name="fraction"/> is 0..1 where a total is known, null
    /// where the stream carries no length. A null <paramref name="status"/> ends the preparation.
    /// </summary>
    void ReportPreparing(string? status, double? fraction);
}
