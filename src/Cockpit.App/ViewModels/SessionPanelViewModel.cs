using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The surface every cockpit session panel shares regardless of mode (SDK chat or TTY terminal):
/// the sidebar/overview title, selection, coarse status, and profile label, plus disposal. Lets
/// <see cref="CockpitViewModel"/> manage a mixed collection of <see cref="SessionViewModel"/>
/// (SDK) and <see cref="ClaudeTtyViewModel"/> (TTY) panels through one type.
/// </summary>
public abstract partial class SessionPanelViewModel : ViewModelBase, IAsyncDisposable
{
    /// <summary>Display title for this session's sidebar/grid panel, e.g. "Claude 1". Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private string _title = "Claude";

    /// <summary>True while the sidebar row is showing its inline rename text box (context-menu → Rename).</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>The in-progress title while renaming; committed to <see cref="Title"/> or discarded.</summary>
    [ObservableProperty]
    private string _editTitle = string.Empty;

    /// <summary>
    /// The choices this session was created with (profile/kind/mode/model/effort), captured by
    /// <see cref="CockpitViewModel"/> so the context-menu Duplicate can start another just like it.
    /// </summary>
    public NewSessionResult? LaunchResult { get; set; }

    /// <summary>Starts an inline rename, seeding the editable title from the current one.</summary>
    public void BeginRename()
    {
        EditTitle = Title;
        IsRenaming = true;
    }

    /// <summary>Commits the inline rename (keeping the current title if the edit is blank).</summary>
    public void CommitRename()
    {
        var trimmed = EditTitle?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            Title = trimmed;
        }

        IsRenaming = false;
    }

    /// <summary>Cancels the inline rename, discarding the edit.</summary>
    public void CancelRename() => IsRenaming = false;

    /// <summary>True while this is <see cref="CockpitViewModel.SelectedSession"/> — drives the sidebar's active-item highlight. Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Whether this panel's view is shown in the session grid: always in multi-session (grid) mode, and
    /// only when selected in single-pane mode (#24 / Zoom). Set by <see cref="CockpitViewModel"/> whenever
    /// the selection or layout changes, so the one live grid can host every session's view (built once,
    /// keeping its TTY pty) and merely hide the deselected ones instead of a second control rebuilding
    /// them on each switch.
    /// </summary>
    [ObservableProperty]
    private bool _isPaneVisible = true;

    /// <summary>Coarse status for the sidebar/grid overview — see <see cref="ViewModels.SessionStatus"/>.</summary>
    [ObservableProperty]
    private SessionStatus _sessionStatus = SessionStatus.Idle;

    /// <summary>Label of the profile the running session was started under, once known.</summary>
    [ObservableProperty]
    private string? _activeProfileLabel;

    /// <summary>
    /// When true, transcript rows show their arrival timestamp (T7). Set by <see cref="CockpitViewModel"/>
    /// from the saved transcript-display setting and updated live when it is toggled in Options. Lives on
    /// the shared base so both session kinds carry it uniformly, though only the SDK chat renders it.
    /// </summary>
    [ObservableProperty]
    private bool _showTimestamps;

    /// <summary>
    /// When true, sending "exit" closes this session once its turn completes (T10). Set by
    /// <see cref="CockpitViewModel"/> from the saved session-behaviour setting and updated live on toggle.
    /// </summary>
    [ObservableProperty]
    private bool _autoCloseOnExit;

    /// <summary>
    /// Raised when the session asks to be closed by itself (T10: after an "exit" turn completes), so
    /// <see cref="CockpitViewModel"/> can run its normal close/teardown flow. The panel never closes
    /// itself — the cockpit owns the session collection.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>Signals <see cref="CockpitViewModel"/> to close this session through its own flow.</summary>
    protected void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Test seam: raise <see cref="CloseRequested"/> directly to exercise the cockpit's close wiring.</summary>
    internal void RequestSelfClose() => RaiseCloseRequested();

    /// <summary>
    /// True while a close is awaiting confirmation for this panel, so its sidebar row shows an inline
    /// "Close? / Keep" prompt rather than dropping a busy session on a single click (mirrors the
    /// Manage-profiles remove confirm, L11).
    /// </summary>
    [ObservableProperty]
    private bool _isConfirmingClose;

    /// <summary>
    /// True when closing would interrupt a running turn, so the close asks first. Idle/waiting/done
    /// sessions close on a single click.
    /// </summary>
    public bool RequiresCloseConfirmation => SessionStatus == SessionStatus.Busy;

    /// <summary>Short human-readable label for <see cref="SessionStatus"/>, for the sidebar status row.</summary>
    public string SessionStatusLabel => SessionStatus switch
    {
        SessionStatus.Busy => "Busy",
        SessionStatus.WaitingForInput => "Waiting for input",
        SessionStatus.NeedsAttention => "Needs attention",
        SessionStatus.Done => "Done",
        _ => "Idle",
    };

    /// <summary>What the running session's driver supports (#26), so the view hides controls a local provider does not offer instead of showing dead ones. Defaults to the full Claude-CLI set until a session starts.</summary>
    [ObservableProperty]
    private SessionCapabilities _capabilities = SessionCapabilities.ClaudeCli;

    /// <summary>Short provider label shown next to a non-Claude session ("Ollama"/"LM Studio"); empty for a Claude session, which needs no badge.</summary>
    [ObservableProperty]
    private string _providerBadge = string.Empty;

    /// <summary>
    /// This session's working directory, once known — the SDK session learns it from its <c>init</c> event,
    /// the TTY session from its launch path. Exposed to plugins through the read/observe surface
    /// (<c>ICockpitSessionObserver.ActiveSessionWorkingDirectory</c>) so a directory-scoped contribution can
    /// follow the session in view. Null until known.
    /// </summary>
    [ObservableProperty]
    private string? _workingDirectory;

    /// <summary>
    /// Raised for each chunk of visible text this session produces (assistant text, tool output, or — for the
    /// TTY session — a tailed transcript line), surfaced to plugins via the read/observe surface so a watcher
    /// can scan for an output signal such as a new pull-request url. Fired on the thread the producing code
    /// runs on; the host-side observer marshals to the UI thread before handing it to plugins.
    /// </summary>
    public event EventHandler<string>? OutputTextProduced;

    /// <summary>Surfaces a chunk of produced text to <see cref="OutputTextProduced"/> subscribers (the read/observe surface). No-op for empty text.</summary>
    protected void RaiseOutputText(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            OutputTextProduced?.Invoke(this, text);
        }
    }

    private IVoicePushToTalkService? _voicePushToTalk;
    private IVoiceSettingsStore? _voiceSettingsStore;
    private IVoicePlaybackQueue? _voicePlaybackQueue;
    private ITranscriptCleanupService? _cleanupService;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.NaturalizeReadAloud"/>: rewrite assistant text into natural spoken form via the local LLM before read-aloud synthesis (#35).</summary>
    [ObservableProperty]
    private bool _naturalizeReadAloud;

    /// <summary>Mirrors the saved voice-input setting, loaded once via <see cref="InitializeVoice"/>. Gates <see cref="BeginVoiceHold"/> so a disabled operator's F9 does nothing.</summary>
    [ObservableProperty]
    private bool _voiceEnabled;

    /// <summary>Avalonia <c>Key</c> enum name for the configured push-to-talk hotkey (e.g. "F9"); the view parses it to compare against <c>KeyEventArgs.Key</c>.</summary>
    [ObservableProperty]
    private string _pushToTalkKeyName = "F9";

    /// <summary>
    /// Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.GlobalPushToTalk"/>. When true, the
    /// <c>VoicePushToTalkCoordinator</c> already routes the OS-wide hotkey to whichever session is
    /// selected, so this session's own local KeyDown/KeyUp handler must no-op — see
    /// <c>PushToTalkKeyGate</c> — to avoid firing the same hold twice.
    /// </summary>
    [ObservableProperty]
    private bool _globalPushToTalkEnabled;

    /// <summary>Transient status text ("Listening...", "Transcribing...") the view can surface next to the input while a hold is in progress.</summary>
    [ObservableProperty]
    private string _voiceStatus = string.Empty;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.AutoSubmitAfterVoice"/>: when true a finished transcript is submitted right after injection (see <see cref="OnVoiceSubmitRequested"/>) instead of waiting for a manual send.</summary>
    [ObservableProperty]
    private bool _autoSubmitAfterVoice;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.TtsVoiceId"/> — the Piper voice used for read-aloud (#35). Loaded on the shared base even though only the SDK session kind triggers synthesis, the same "load every voice field once" approach as the other voice settings here.</summary>
    [ObservableProperty]
    private string _ttsVoiceId = "en_US-lessac-medium";

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.TtsVoiceIdDutch"/> — the Piper voice the Dutch segments of a mixed-language read-aloud reply route to when naturalization tags the languages (#35).</summary>
    [ObservableProperty]
    private string _dutchTtsVoiceId = "nl_NL-ronnie-medium";

    /// <summary>
    /// Per-session read-aloud toggle (#35/#35b): when true, completed assistant replies are extracted
    /// and enqueued for TTS playback. Shared on the base since both session kinds offer the toggle, even
    /// though the source differs — the SDK session reads its already-open event stream at turn
    /// completion, the TTY session tails the live JSONL transcript (see
    /// <see cref="OnReadAloudToggleChanged"/>). Ephemeral runtime state, off by default.
    /// </summary>
    [ObservableProperty]
    private bool _readResponsesAloud;

    partial void OnReadResponsesAloudChanged(bool value)
    {
        // Turning read-aloud off must silence it now — stop in-flight and queued playback immediately,
        // not just suppress future turns.
        if (!value)
        {
            _voicePlaybackQueue?.StopAll();
        }

        OnReadAloudToggleChanged(value);
    }

    /// <summary>
    /// Hook for a session kind whose read-aloud source needs starting/stopping when the toggle flips.
    /// No-op by default (the SDK session needs no separate start/stop — it just checks the flag at each
    /// turn completion); the TTY session overrides this to begin/end tailing the transcript.
    /// </summary>
    protected virtual void OnReadAloudToggleChanged(bool isEnabled)
    {
    }

    /// <summary>
    /// Wires the shared push-to-talk plumbing and loads the current voice settings. Called from the
    /// concrete view model's constructor rather than folded into the base constructor, since the two
    /// session kinds take a different set of optional services.
    /// </summary>
    protected void InitializeVoice(
        IVoicePushToTalkService? voicePushToTalk,
        IVoiceSettingsStore? voiceSettingsStore,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        ITranscriptCleanupService? cleanupService = null)
    {
        _voicePushToTalk = voicePushToTalk;
        _voiceSettingsStore = voiceSettingsStore;
        _voicePlaybackQueue = voicePlaybackQueue;
        _cleanupService = cleanupService;

        if (voiceSettingsStore is not null)
        {
            _ = _LoadVoiceSettingsAsync(voiceSettingsStore);
        }
    }

    private async Task _LoadVoiceSettingsAsync(IVoiceSettingsStore voiceSettingsStore)
    {
        var settings = await voiceSettingsStore.LoadAsync();
        VoiceEnabled = settings.IsEnabled;
        PushToTalkKeyName = settings.PushToTalkKeyName;
        GlobalPushToTalkEnabled = settings.GlobalPushToTalk;
        AutoSubmitAfterVoice = settings.AutoSubmitAfterVoice;
        TtsVoiceId = settings.TtsVoiceId;
        DutchTtsVoiceId = settings.TtsVoiceIdDutch;
        NaturalizeReadAloud = settings.NaturalizeReadAloud;
    }

    /// <summary>
    /// Enqueues sentences for read-aloud playback (turn-completion trigger or the on-demand per-row
    /// button, both SDK-only) — a no-op when the playback queue was never wired (design-time/tests) or
    /// there is nothing to say.
    /// </summary>
    protected void EnqueueReadAloud(IReadOnlyList<string> sentences, string voiceId)
    {
        if (sentences.Count == 0)
        {
            return;
        }

        _voicePlaybackQueue?.Enqueue(sentences, voiceId);
    }

    /// <summary>
    /// Extracts the prose from assistant text and enqueues it for read-aloud (#35), first rewriting it into
    /// natural spoken sentences via the local LLM when <see cref="NaturalizeReadAloud"/> is on (falling back
    /// to the plain extracted prose if the LLM is unavailable). The extractor already strips code/tables and
    /// swaps paths/URLs for spoken words; the LLM pass smooths the rest and tags language runs
    /// (<c>[[nl]]</c>/<c>[[en]]</c>) so mixed Dutch/English replies route each segment to the matching voice.
    /// </summary>
    protected async Task EnqueueReadAloudAsync(string text, string voiceId)
    {
        var sentences = TtsProseExtractor.Extract(text);
        if (sentences.Count == 0)
        {
            return;
        }

        if (NaturalizeReadAloud && _cleanupService is not null)
        {
            var natural = await _cleanupService.NaturalizeForSpeechAsync(string.Join(" ", sentences));
            var segments = SpeechLanguageRouter.Route(natural, voiceId, DutchTtsVoiceId);
            if (segments.Count > 0)
            {
                _voicePlaybackQueue?.Enqueue(segments);
                return;
            }
        }

        EnqueueReadAloud(sentences, voiceId);
    }

    /// <summary>
    /// Starts a push-to-talk hold (KeyDown on the configured hotkey). Returns false — a no-op the
    /// caller should not mark <c>Handled</c> for — when voice is off, unwired, or a hold is already in
    /// progress (the underlying service's own key-repeat guard).
    /// </summary>
    public bool BeginVoiceHold()
    {
        if (!VoiceEnabled || _voicePushToTalk is null)
        {
            return false;
        }

        // A push-to-talk hold means "listen to me now" — interrupt whatever read-aloud playback is
        // running (on this session or any other; the queue is one shared singleton, #35) so it never
        // talks over the dictation.
        _voicePlaybackQueue?.StopAll();

        var started = _voicePushToTalk.BeginHold();
        if (started)
        {
            VoiceStatus = "Listening...";
        }

        return started;
    }

    /// <summary>
    /// Ends the push-to-talk hold (KeyUp), transcribes it, and hands any resulting text to
    /// <see cref="OnVoiceTextReady"/> for this session kind to inject. No-op when voice was never wired.
    /// </summary>
    public async Task EndVoiceHoldAsync(bool applyCleanup)
    {
        if (_voicePushToTalk is null)
        {
            return;
        }

        VoiceStatus = "Transcribing...";
        try
        {
            var text = await _voicePushToTalk.EndHoldAsync(applyCleanup);
            VoiceStatus = string.Empty;
            if (!string.IsNullOrEmpty(text))
            {
                OnVoiceTextReady(text);
                if (AutoSubmitAfterVoice)
                {
                    OnVoiceSubmitRequested();
                }
            }
        }
        catch (Exception ex)
        {
            VoiceStatus = $"Voice error: {ex.Message}";
        }
    }

    /// <summary>
    /// Injects text into this session's input surface (chat input box for SDK, raw pty bytes for TTY) —
    /// the public seam plugins use via <c>ICockpitActions.InjectIntoActiveSessionAsync</c>, reusing the
    /// same per-kind path as a finished voice transcript.
    /// </summary>
    public void InjectText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            OnVoiceTextReady(text);
        }
    }

    /// <summary>
    /// Injects an open-mic transcript into this session and submits it when <see cref="AutoSubmitAfterVoice"/>
    /// is on — the finished-transcript half of <see cref="EndVoiceHoldAsync"/>, for the hands-free open-mic
    /// path that produces text without a hold.
    /// </summary>
    public void InjectVoiceTranscript(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        OnVoiceTextReady(text);
        if (AutoSubmitAfterVoice)
        {
            OnVoiceSubmitRequested();
        }
    }

    /// <summary>Injects a finished voice transcript into this session kind's own input surface (chat input box or raw pty bytes).</summary>
    protected abstract void OnVoiceTextReady(string text);

    /// <summary>
    /// Submits the just-injected transcript when <see cref="AutoSubmitAfterVoice"/> is on — the SDK
    /// session sends its input box, the TTY session writes a trailing carriage return. Default no-op so
    /// a session kind without a submit gesture simply leaves the text in place.
    /// </summary>
    protected virtual void OnVoiceSubmitRequested()
    {
    }

    /// <summary>Theme brush resource key for the status dot — resolved in the view via a converter.</summary>
    public string SessionStatusBrushKey => SessionStatus switch
    {
        SessionStatus.Busy => "CockpitStatusBusyBrush",
        SessionStatus.WaitingForInput or SessionStatus.NeedsAttention => "CockpitStatusWaitingBrush",
        SessionStatus.Done => "CockpitStatusDoneBrush",
        _ => "CockpitTextFaintBrush",
    };

    /// <summary>Keeps the derived status label/brush in sync whenever <see cref="SessionStatus"/> changes.</summary>
    partial void OnSessionStatusChanged(SessionStatus value)
    {
        OnPropertyChanged(nameof(SessionStatusLabel));
        OnPropertyChanged(nameof(SessionStatusBrushKey));
        OnPropertyChanged(nameof(RequiresCloseConfirmation));
    }

    public async ValueTask DisposeAsync()
    {
        // Closing a session that is reading responses aloud must silence it too — otherwise its queued
        // and in-flight utterances keep playing after the panel is gone. The playback queue is one shared
        // singleton (#35), so this is the same blanket stop push-to-talk uses; gating it on this session's
        // own toggle keeps closing a silent session from cutting another that is mid-sentence.
        if (ReadResponsesAloud)
        {
            _voicePlaybackQueue?.StopAll();
        }

        await DisposeCoreAsync();
    }

    /// <summary>Kind-specific teardown (kill the CLI process, stop the transcript tailer), run after read-aloud is silenced.</summary>
    protected abstract ValueTask DisposeCoreAsync();
}
