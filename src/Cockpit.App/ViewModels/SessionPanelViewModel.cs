using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The surface every cockpit session panel shares regardless of mode (SDK chat or TTY terminal):
/// the sidebar/overview title, selection, coarse status, and profile label, plus disposal. Lets
/// <see cref="CockpitViewModel"/> manage a mixed collection of <see cref="ClaudeSessionViewModel"/>
/// (SDK) and <see cref="ClaudeTtyViewModel"/> (TTY) panels through one type.
/// </summary>
public abstract partial class SessionPanelViewModel : ViewModelBase, IAsyncDisposable
{
    /// <summary>Display title for this session's sidebar/grid panel, e.g. "Claude 1". Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private string _title = "Claude";

    /// <summary>True while this is <see cref="CockpitViewModel.SelectedSession"/> — drives the sidebar's active-item highlight. Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private bool _isSelected;

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

    private IVoicePushToTalkService? _voicePushToTalk;
    private IVoiceSettingsStore? _voiceSettingsStore;

    /// <summary>Mirrors the saved voice-input setting, loaded once via <see cref="InitializeVoice"/>. Gates <see cref="BeginVoiceHold"/> so a disabled operator's F9 does nothing.</summary>
    [ObservableProperty]
    private bool _voiceEnabled;

    /// <summary>Avalonia <c>Key</c> enum name for the configured push-to-talk hotkey (e.g. "F9"); the view parses it to compare against <c>KeyEventArgs.Key</c>.</summary>
    [ObservableProperty]
    private string _pushToTalkKeyName = "F9";

    /// <summary>Transient status text ("Listening...", "Transcribing...") the view can surface next to the input while a hold is in progress.</summary>
    [ObservableProperty]
    private string _voiceStatus = string.Empty;

    /// <summary>
    /// Wires the shared push-to-talk plumbing and loads the current voice settings. Called from the
    /// concrete view model's constructor rather than folded into the base constructor, since the two
    /// session kinds take a different set of optional services.
    /// </summary>
    protected void InitializeVoice(IVoicePushToTalkService? voicePushToTalk, IVoiceSettingsStore? voiceSettingsStore)
    {
        _voicePushToTalk = voicePushToTalk;
        _voiceSettingsStore = voiceSettingsStore;

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
            }
        }
        catch (Exception ex)
        {
            VoiceStatus = $"Voice error: {ex.Message}";
        }
    }

    /// <summary>Injects a finished voice transcript into this session kind's own input surface (chat input box or raw pty bytes).</summary>
    protected abstract void OnVoiceTextReady(string text);

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

    public abstract ValueTask DisposeAsync();
}
