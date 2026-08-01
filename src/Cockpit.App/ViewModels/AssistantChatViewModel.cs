using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The surface this window needs from the assistant's owning host (AC-543, "Waar de assistent-sessie vandaan
/// komt"). The lead's <c>AssistantSessionHost</c> (<c>src/Cockpit.App/Services/AssistantSessionHost.cs</c>) already
/// carries exactly this shape — <c>Session</c>, <c>Activity</c>, <c>UnavailableReason</c>, <c>EnsureStartedAsync</c>,
/// <c>SendAsync</c> — but as a sealed class with no interface, so it cannot itself be swapped for a test fake and
/// is heavy to construct directly (it needs a live <c>CockpitViewModel</c>). Extracted as an interface purely for
/// that: the one remaining integration step is a one-line <c>: IAssistantSessionHost</c> on that class — see the
/// final report.
/// </summary>
public interface IAssistantSessionHost : INotifyPropertyChanged
{
    /// <summary>The assistant's own long-running session, or null while it has not been lazily started yet.</summary>
    SessionViewModel? Session { get; }

    /// <summary>What the indicator shows; pairs with <see cref="UnavailableReason"/> when it is <see cref="AssistantActivity.Unavailable"/>.</summary>
    AssistantActivity Activity { get; }

    /// <summary>Why the assistant cannot be reached right now (feature off, no profile, failed start) — null otherwise.</summary>
    string? UnavailableReason { get; }

    /// <summary>Idempotent lazy start: returns the running session if there is one, restarts it if it fell over, and no-ops (leaving <see cref="Activity"/> at Unavailable) if the feature is off or the slot is empty.</summary>
    Task<SessionViewModel?> EnsureStartedAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends typed or spoken text to the assistant, starting it lazily first if it has not run yet.</summary>
    Task SendAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads the settings and stands the assistant down if the feature was switched off — mid-sentence
    /// included, which is the point of it being a separate call rather than something checked on the next use.
    /// </summary>
    Task ApplySettingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Backs the pop-out chat window (AC-543 criteria 7, 8, 9): a peephole onto the assistant's own standing
/// conversation, never its owner.
/// </summary>
/// <remarks>
/// <b>Criterion 7 — reads a conversation, never starts one.</b> <see cref="Session"/> is read straight off
/// <see cref="IAssistantSessionHost.Session"/>; this view model never creates a <see cref="SessionViewModel"/>
/// itself. <see cref="EnsureOpenedAsync"/> is the one call this view model makes that can cause a start, and it
/// only runs the host's own idempotent lazy-start (opening the chip is "an operator handling" per criterion 1) —
/// it never resets or replaces whatever conversation the host already holds. <see cref="Dispose"/> only detaches
/// this peephole's own event subscription; it never touches the session, so closing the window can never end it.
/// <para>
/// <b>Criterion 8 — no microphone required.</b> This view model carries no STT/voice code path at all: sending is
/// <see cref="SendAsync"/> on typed <see cref="InputText"/>, full stop. The assistant hotkey (<c>F10</c>) is a
/// global hook the lead wires elsewhere (<c>GlobalHotkeyCoordinator</c>); this window does not depend on it being
/// available, configured, or even present.
/// </para>
/// <para>
/// <b>Criterion 9 — off breaks off, mid-sentence.</b> Switching <see cref="SpeakReplies"/> off calls
/// <see cref="IVoicePlaybackQueue.StopAll"/> before persisting the new value — the same interrupt a push-to-talk
/// barge-in uses — so a reply already playing is cut, not finished. Switching it back on plays nothing on its own;
/// it only changes what happens to the <em>next</em> reply.
/// </para>
/// </remarks>
public sealed partial class AssistantChatViewModel : ObservableObject, IDisposable
{
    private readonly IAssistantSessionHost _host;
    private readonly IAssistantSettingsStore _settingsStore;
    private readonly IVoicePlaybackQueue _playbackQueue;

    // Session is exposed only through the property below, not as [ObservableProperty] on a field, because it is
    // never assigned locally — it always reads straight through to the host. What can change is *which* session
    // the host reports, so change notification is driven by watching the host's own PropertyChanged instead
    // (see _OnHostPropertyChanged) rather than by a setter nothing in this class ever calls.
    private SessionViewModel? _observedSession;

    // Set while _LoadSpeakRepliesAsync is applying a freshly loaded value to SpeakReplies, so that assignment does
    // not read as the operator flicking the switch: without this guard, a stored "off" would fire the same
    // playback-interrupt and re-save that a real click does, on every window open, for a value that was never
    // touched.
    private bool _loadingSpeakReplies;

    [ObservableProperty]
    private string _inputText = string.Empty;

    // Mirrors AssistantSettings.SpeakReplies's own default (true) until the real value loads, so the header does
    // not flash "off" for the one frame before EnsureOpenedAsync's load completes.
    [ObservableProperty]
    private bool _speakReplies = true;

    public AssistantChatViewModel(IAssistantSessionHost host, IAssistantSettingsStore settingsStore, IVoicePlaybackQueue playbackQueue)
    {
        _host = host;
        _settingsStore = settingsStore;
        _playbackQueue = playbackQueue;
        _observedSession = _host.Session;
        _host.PropertyChanged += _OnHostPropertyChanged;
    }

    /// <summary>The assistant's own session, bound straight through to the existing SDK transcript view. Null until the assistant has been lazily started at least once.</summary>
    public SessionViewModel? Session => _host.Session;

    /// <summary>Whether there is anything to show yet — a fresh install, or a window opened before the first message, both read as "no session" rather than an empty transcript on a phantom one.</summary>
    public bool HasSession => Session is not null;

    /// <summary>Whether the transcript has any rows — separate from <see cref="HasSession"/> because a session can exist (started) with nothing said in it yet.</summary>
    public bool HasMessages => Session?.HasTranscript ?? false;

    /// <summary>True while the assistant cannot be reached at all (criterion 1: feature off, no profile, or a failed start) — paired with <see cref="UnavailableReason"/> so the window says why instead of just sitting empty.</summary>
    public bool IsUnavailable => _host.Activity == AssistantActivity.Unavailable;

    public string? UnavailableReason => _host.UnavailableReason;

    public bool CanSend => !string.IsNullOrWhiteSpace(InputText);

    /// <summary>
    /// Opens the window's view onto the assistant (criterion 1: the first chip click is the "operator handling"
    /// that is allowed to start it lazily) — called by the view once, when it attaches. Never called from the
    /// constructor: a view model built to seed design-time/Screenshotter data must not reach out and start a real
    /// session the moment it exists. Loads the current read-aloud setting first, so the header toggle opens
    /// showing what Options actually has it set to rather than the placeholder default.
    /// </summary>
    public async Task EnsureOpenedAsync(CancellationToken cancellationToken = default)
    {
        await _LoadSpeakRepliesAsync(cancellationToken).ConfigureAwait(true);
        await _host.EnsureStartedAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task _LoadSpeakRepliesAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        _loadingSpeakReplies = true;
        try
        {
            SpeakReplies = settings.SpeakReplies;
        }
        finally
        {
            _loadingSpeakReplies = false;
        }
    }

    /// <summary>
    /// The read-aloud switch (criterion 9) — the same header toggle whether it is opened here or in Options, since
    /// both read and write the same <see cref="AssistantSettings.SpeakReplies"/> through <see cref="IAssistantSettingsStore"/>.
    /// Switching it off calls <see cref="IVoicePlaybackQueue.StopAll"/> before persisting — the same interrupt a
    /// push-to-talk barge-in uses — so a reply already playing is cut, not finished. Guarded by
    /// <see cref="_loadingSpeakReplies"/> so applying a freshly loaded value on open never reads as a click.
    /// </summary>
    partial void OnSpeakRepliesChanged(bool value)
    {
        if (_loadingSpeakReplies)
        {
            return;
        }

        if (!value)
        {
            // Whoever clicks off wants silence, not one more paragraph — cut what is already playing before the
            // setting even finishes persisting, so there is no window where the old value lingers audibly.
            _playbackQueue.StopAll();
        }

        _ = _PersistSpeakRepliesAsync(value);
    }

    private async Task _PersistSpeakRepliesAsync(bool value)
    {
        // Read-modify-write on the whole record: IAssistantSettingsStore persists AssistantSettings as a unit, and
        // this window only ever means to change the one field, never to clobber whatever Options last saved for
        // IsEnabled/ListeningMode/etc. with values already loaded here.
        var current = await _settingsStore.LoadAsync().ConfigureAwait(true);
        await _settingsStore.SaveAsync(current with { SpeakReplies = value }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        InputText = string.Empty;
        // Goes through the host, not Session.SendCommand: Session can still be null here (nothing typed yet since
        // this instance came up), and the host's SendAsync is what performs the lazy start — the first message
        // typed into an unstarted assistant is exactly what starts it (criterion 1).
        await _host.SendAsync(text);
    }

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    private void _OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(IAssistantSessionHost.Session))
        {
            var session = _host.Session;
            if (!ReferenceEquals(_observedSession, session))
            {
                _observedSession = session;
                OnPropertyChanged(nameof(Session));
                OnPropertyChanged(nameof(HasSession));
                OnPropertyChanged(nameof(HasMessages));
            }
        }

        if (e.PropertyName is null or nameof(IAssistantSessionHost.Activity))
        {
            OnPropertyChanged(nameof(IsUnavailable));
        }

        if (e.PropertyName is null or nameof(IAssistantSessionHost.UnavailableReason))
        {
            OnPropertyChanged(nameof(UnavailableReason));
        }
    }

    /// <summary>
    /// Detaches from the host — nothing more. Deliberately does not touch <see cref="Session"/> in any way: this
    /// runs when the window closes, and closing this window must never end the assistant's conversation
    /// (criterion 7). It only stops this peephole listening for host changes it can no longer show anyone.
    /// </summary>
    public void Dispose() => _host.PropertyChanged -= _OnHostPropertyChanged;
}
