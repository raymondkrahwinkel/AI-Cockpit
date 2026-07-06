using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.SessionSwitching;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.SessionSwitching;
using Cockpit.Core.TranscriptDisplay;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Multi-instance cockpit shell: owns the collection of running <see cref="ClaudeSessionViewModel"/>
/// panels, which one is selected, and the grid/zoom view mode. Reuses the existing
/// <see cref="ClaudeSessionViewModel"/>/<c>ClaudeSessionView</c> per panel — this view model only
/// adds the manager layer around it. See <c>Memory/Cockpit/Plan.md</c> §Vision-uitbreiding + §UX-eisen.
/// </summary>
/// <remarks>
/// Also carries the F0 audio record/play commands so the sidebar's secondary "Tools" footer (see
/// <c>CockpitView.axaml</c>) can bind to them without reaching into a sibling view model — the
/// cockpit is the single root VM behind the window; audio is a small, secondary tool hanging off it.
/// </remarks>
// Singleton: it is the single root view model behind the window, and the shutdown path resolves it
// back to dispose the live sessions (bug #32) — that must be the same instance the window holds.
public partial class CockpitViewModel : ViewModelBase, ISingletonService, IAsyncDisposable
{
    private static readonly Core.Audio.AudioFormat AudioFormat = new();

    private readonly Func<ClaudeSessionViewModel>? _sessionFactory;
    private readonly Func<ClaudeTtyViewModel>? _ttySessionFactory;
    private readonly ISessionDialogService? _dialogService;
    private readonly IAudioCaptureService? _captureService;
    private readonly IAudioPlaybackService? _playbackService;
    private readonly IAttentionNotifier? _attentionNotifier;
    private readonly INotificationSettingsStore? _notificationSettingsStore;
    private readonly ISessionSwitchSettingsStore? _sessionSwitchSettingsStore;
    private readonly ITranscriptDisplaySettingsStore? _transcriptDisplaySettingsStore;
    private readonly ISessionBehaviorSettingsStore? _sessionBehaviorSettingsStore;
    private readonly List<byte> _recordedPcm = [];

    // Last observed status per session, so a NeedsAttention notification fires only on the edge into
    // that state — not on every property change while a session already needs attention.
    private readonly Dictionary<SessionPanelViewModel, SessionStatus> _lastStatus = [];
    private CancellationTokenSource? _recordingCancellation;
    private int _sessionCounter;

    public ObservableCollection<SessionPanelViewModel> Sessions { get; } = [];

    /// <summary>False when no session is open, driving the empty-state welcome screen vs. the session grid (#31).</summary>
    public bool HasSessions => Sessions.Count > 0;

    /// <summary>
    /// Column count for the adaptive session grid (#24): one session fills the width; two or more lay
    /// out in two columns (so 3–4 form a 2×2), rather than the old fixed two that left a single session
    /// pinned to the left half.
    /// </summary>
    public int GridColumns => Sessions.Count <= 1 ? 1 : 2;

    /// <summary>
    /// TTY mode hosts the real claude TUI via ConPTY, which is Windows-only in this build; the sidebar
    /// hides "+ New session (TTY)" on other platforms rather than offer a session whose view can't load
    /// (it resolved to a "Not Found" placeholder on Linux).
    /// </summary>
    public bool IsTtySupported { get; } = OperatingSystem.IsWindows();

    [ObservableProperty]
    private SessionPanelViewModel? _selectedSession;

    /// <summary>True while the grid is collapsed to show only <see cref="SelectedSession"/> at full width.</summary>
    [ObservableProperty]
    private bool _isZoomed;

    [ObservableProperty]
    private string _audioStatus = "Ready.";

    /// <summary>Master switch for presence-aware notifications (toast when present, Discord webhook when away).</summary>
    [ObservableProperty]
    private bool _notificationsEnabled = true;

    /// <summary>Discord webhook URL POSTed to when the operator is away. Empty disables the away channel.</summary>
    [ObservableProperty]
    private string _webhookUrl = string.Empty;

    /// <summary>Idle minutes before the operator counts as "away" (when the PC is not locked).</summary>
    [ObservableProperty]
    private int _idleThresholdMinutes = (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

    [ObservableProperty]
    private string _notificationSettingsStatus = string.Empty;

    /// <summary>Master switch for the arrow-key session switch (Ctrl+Arrow by default).</summary>
    [ObservableProperty]
    private bool _sessionSwitchEnabled = true;

    /// <summary>The modifier that arms the arrow-key session switch. See <see cref="SessionSwitchModifier"/>.</summary>
    [ObservableProperty]
    private SessionSwitchModifierOption _selectedSessionSwitchModifier =
        new("Ctrl", SessionSwitchModifier.Ctrl);

    [ObservableProperty]
    private string _sessionSwitchSettingsStatus = string.Empty;

    /// <summary>When true, every transcript row shows its arrival timestamp (T7). Applied to all open sessions.</summary>
    [ObservableProperty]
    private bool _showTimestamps;

    [ObservableProperty]
    private string _transcriptDisplaySettingsStatus = string.Empty;

    /// <summary>When true, sending "exit" closes the session after its turn completes (T10). Applied to all open sessions.</summary>
    [ObservableProperty]
    private bool _autoCloseOnExit;

    [ObservableProperty]
    private string _sessionBehaviorSettingsStatus = string.Empty;

    /// <summary>Pushes the timestamp toggle to every open session as it changes, so the switch takes effect live.</summary>
    partial void OnShowTimestampsChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.ShowTimestamps = value;
        }
    }

    /// <summary>Pushes the auto-close-on-exit toggle to every open session as it changes.</summary>
    partial void OnAutoCloseOnExitChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.AutoCloseOnExit = value;
        }
    }

    /// <summary>Selectable modifiers for the session-switch gesture (bound by the Options flyout combo box).</summary>
    public IReadOnlyList<SessionSwitchModifierOption> SessionSwitchModifiers { get; } =
    [
        new("Ctrl", SessionSwitchModifier.Ctrl),
        new("Ctrl + Alt", SessionSwitchModifier.CtrlAlt),
        new("Alt", SessionSwitchModifier.Alt),
    ];

    /// <summary>
    /// The current switch settings as the view needs them for its KeyDown gate: whether the gesture is
    /// enabled and which modifier arms it. Reflects the live (possibly unsaved) Options-flyout edits.
    /// </summary>
    public SessionSwitchSettings CurrentSessionSwitchSettings => new()
    {
        IsEnabled = SessionSwitchEnabled,
        Modifier = SelectedSessionSwitchModifier.Value,
    };

    /// <summary>Keeps each session's <see cref="ClaudeSessionViewModel.IsSelected"/> in sync with the active selection.</summary>
    partial void OnSelectedSessionChanged(SessionPanelViewModel? oldValue, SessionPanelViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    // Parameterless constructor kept for the Avalonia previewer/Screenshotter design-time context —
    // seeds three sample sessions with different statuses/profiles so the render shows the overview
    // + grid without a real DI-backed session behind each one.
    public CockpitViewModel()
    {
        var waiting = new ClaudeSessionViewModel { Title = "Claude 1", ActiveProfileLabel = "work", SessionStatus = SessionStatus.NeedsAttention };
        var busy = new ClaudeSessionViewModel { Title = "Claude 2", ActiveProfileLabel = "private", SessionStatus = SessionStatus.Busy };
        var tty = new ClaudeTtyViewModel { Title = "Claude 3", ActiveProfileLabel = "work (TTY)", SessionStatus = SessionStatus.Busy };

        Sessions.Add(waiting);
        Sessions.Add(busy);
        Sessions.Add(tty);
        _sessionCounter = Sessions.Count;
        SelectedSession = waiting;
    }

    public CockpitViewModel(
        Func<ClaudeSessionViewModel> sessionFactory,
        Func<ClaudeTtyViewModel> ttySessionFactory,
        ISessionDialogService dialogService,
        IAudioCaptureService captureService,
        IAudioPlaybackService playbackService,
        IAttentionNotifier attentionNotifier,
        INotificationSettingsStore notificationSettingsStore,
        ISessionSwitchSettingsStore sessionSwitchSettingsStore,
        ITranscriptDisplaySettingsStore transcriptDisplaySettingsStore,
        ISessionBehaviorSettingsStore sessionBehaviorSettingsStore)
    {
        _sessionFactory = sessionFactory;
        _ttySessionFactory = ttySessionFactory;
        _dialogService = dialogService;
        _captureService = captureService;
        _playbackService = playbackService;
        _attentionNotifier = attentionNotifier;
        _notificationSettingsStore = notificationSettingsStore;
        _sessionSwitchSettingsStore = sessionSwitchSettingsStore;
        _transcriptDisplaySettingsStore = transcriptDisplaySettingsStore;
        _sessionBehaviorSettingsStore = sessionBehaviorSettingsStore;
        // No session is opened on startup (#31): the app starts on the empty state and a session only
        // exists once the operator creates one from the New-session dialog.
        Sessions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(GridColumns));
        };
        _ = LoadNotificationSettingsAsync();
        _ = LoadSessionSwitchSettingsAsync();
        _ = LoadTranscriptDisplaySettingsAsync();
        _ = LoadSessionBehaviorSettingsAsync();
    }

    private async Task LoadNotificationSettingsAsync()
    {
        if (_notificationSettingsStore is null)
        {
            return;
        }

        var settings = await _notificationSettingsStore.LoadAsync();
        NotificationsEnabled = settings.IsEnabled;
        WebhookUrl = settings.WebhookUrl ?? string.Empty;
        IdleThresholdMinutes = (int)settings.IdleThreshold.TotalMinutes;
    }

    /// <summary>Persists the notification settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveNotificationSettingsAsync()
    {
        if (_notificationSettingsStore is null)
        {
            return;
        }

        var minutes = IdleThresholdMinutes > 0
            ? IdleThresholdMinutes
            : (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

        var settings = new NotificationSettings
        {
            IsEnabled = NotificationsEnabled,
            WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim(),
            IdleThreshold = TimeSpan.FromMinutes(minutes),
        };

        await _notificationSettingsStore.SaveAsync(settings);
        NotificationSettingsStatus = "Saved.";
    }

    private async Task LoadSessionSwitchSettingsAsync()
    {
        if (_sessionSwitchSettingsStore is null)
        {
            return;
        }

        var settings = await _sessionSwitchSettingsStore.LoadAsync();
        SessionSwitchEnabled = settings.IsEnabled;
        SelectedSessionSwitchModifier = SessionSwitchModifiers.FirstOrDefault(option => option.Value == settings.Modifier)
                                        ?? SessionSwitchModifiers[0];
    }

    /// <summary>Persists the session-switch settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveSessionSwitchSettingsAsync()
    {
        if (_sessionSwitchSettingsStore is null)
        {
            return;
        }

        await _sessionSwitchSettingsStore.SaveAsync(CurrentSessionSwitchSettings);
        SessionSwitchSettingsStatus = "Saved.";
    }

    private async Task LoadTranscriptDisplaySettingsAsync()
    {
        if (_transcriptDisplaySettingsStore is null)
        {
            return;
        }

        var settings = await _transcriptDisplaySettingsStore.LoadAsync();
        ShowTimestamps = settings.ShowTimestamps;
    }

    /// <summary>Persists the transcript-display settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveTranscriptDisplaySettingsAsync()
    {
        if (_transcriptDisplaySettingsStore is null)
        {
            return;
        }

        await _transcriptDisplaySettingsStore.SaveAsync(new TranscriptDisplaySettings { ShowTimestamps = ShowTimestamps });
        TranscriptDisplaySettingsStatus = "Saved.";
    }

    private async Task LoadSessionBehaviorSettingsAsync()
    {
        if (_sessionBehaviorSettingsStore is null)
        {
            return;
        }

        var settings = await _sessionBehaviorSettingsStore.LoadAsync();
        AutoCloseOnExit = settings.AutoCloseOnExit;
    }

    /// <summary>Persists the session-behaviour settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveSessionBehaviorSettingsAsync()
    {
        if (_sessionBehaviorSettingsStore is null)
        {
            return;
        }

        await _sessionBehaviorSettingsStore.SaveAsync(new SessionBehaviorSettings { AutoCloseOnExit = AutoCloseOnExit });
        SessionBehaviorSettingsStatus = "Saved.";
    }

    [RelayCommand]
    private async Task RecordAudioAsync()
    {
        if (_captureService is null)
        {
            return;
        }

        _recordedPcm.Clear();
        _recordingCancellation = new CancellationTokenSource();
        AudioStatus = "Recording...";

        try
        {
            await foreach (var frame in _captureService.CaptureAsync(AudioFormat, _recordingCancellation.Token))
            {
                _recordedPcm.AddRange(frame.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when StopRecordingAudio cancels the capture stream.
        }

        AudioStatus = $"Recorded {_recordedPcm.Count} bytes.";
    }

    [RelayCommand]
    private void StopRecordingAudio()
    {
        _recordingCancellation?.Cancel();
    }

    [RelayCommand]
    private async Task PlayAudioAsync()
    {
        if (_playbackService is null || _recordedPcm.Count == 0)
        {
            AudioStatus = "Nothing recorded yet.";
            return;
        }

        AudioStatus = "Playing...";
        await _playbackService.PlayAsync(_recordedPcm.ToArray(), AudioFormat);
        AudioStatus = "Playback done.";
    }

    /// <summary>
    /// Opens the New-session dialog and, once confirmed, mints an SDK-mode session (headless stream-json
    /// rendered as the chat UI) and starts it immediately with the chosen profile and options (#31).
    /// </summary>
    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (_sessionFactory is null || _dialogService is null)
        {
            return;
        }

        var result = await _dialogService.ShowNewSessionDialogAsync(SessionKind.Sdk);
        if (result is null)
        {
            return;
        }

        var session = _sessionFactory();
        AddSession(session, result.SessionName);
        await session.StartConfiguredAsync(result.Profile, result.Mode, result.Model, result.Effort);
    }

    /// <summary>
    /// Opens the New-session dialog and, once confirmed, mints a TTY-mode session (the real interactive
    /// <c>claude</c> TUI in a terminal panel — the #9 experiment) under the chosen profile.
    /// </summary>
    [RelayCommand]
    private async Task NewTtySessionAsync()
    {
        if (_ttySessionFactory is null || _dialogService is null)
        {
            return;
        }

        var result = await _dialogService.ShowNewSessionDialogAsync(SessionKind.Tty);
        if (result is null)
        {
            return;
        }

        var session = _ttySessionFactory();
        AddSession(session, result.SessionName);
        session.LaunchConfigured(result.Profile);
    }

    /// <summary>Opens the Manage-profiles dialog from the sidebar, independent of creating a session (L2).</summary>
    [RelayCommand]
    private async Task ManageProfilesAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowManageProfilesDialogAsync();
    }

    private void AddSession(SessionPanelViewModel session, string? name = null)
    {
        _sessionCounter++;
        // A friendly name from the dialog wins; otherwise fall back to the running "Claude N" counter.
        session.Title = string.IsNullOrWhiteSpace(name) ? $"Claude {_sessionCounter}" : name.Trim();
        // Start the session on the current transcript-display preference; OnShowTimestampsChanged keeps
        // it live afterwards (T7).
        session.ShowTimestamps = ShowTimestamps;
        // Same for the auto-close-on-exit behaviour (T10); the session raises CloseRequested when an
        // "exit" turn completes and the cockpit runs its normal close flow.
        session.AutoCloseOnExit = AutoCloseOnExit;
        session.CloseRequested += OnSessionCloseRequested;

        _lastStatus[session] = session.SessionStatus;
        session.PropertyChanged += OnSessionPropertyChanged;

        Sessions.Add(session);
        SelectedSession = session;
    }

    /// <summary>
    /// Edge-triggered attention routing: fires the presence-aware notifier once, on the transition
    /// into <see cref="SessionStatus.NeedsAttention"/> — not on every status touch while it stays
    /// there. The notifier itself decides present-toast vs away-webhook.
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionPanelViewModel.SessionStatus) || sender is not SessionPanelViewModel session)
        {
            return;
        }

        var previous = _lastStatus.GetValueOrDefault(session, SessionStatus.Idle);
        _lastStatus[session] = session.SessionStatus;

        if (session.SessionStatus == SessionStatus.NeedsAttention && previous != SessionStatus.NeedsAttention)
        {
            NotifyAttention(session);
        }
    }

    /// <summary>A session asked to close itself (T10: an "exit" turn finished) — run the normal close flow.</summary>
    private void OnSessionCloseRequested(object? sender, EventArgs e)
    {
        if (sender is SessionPanelViewModel session)
        {
            _ = CloseSessionAsync(session);
        }
    }

    private void NotifyAttention(SessionPanelViewModel session)
    {
        if (_attentionNotifier is null)
        {
            return;
        }

        var notification = new AttentionNotification(session.Title, session.SessionStatusLabel);
        // Fire-and-forget: notification delivery must not block the UI thread that raised the status
        // change. The notifier swallows and logs its own transport failures.
        _ = _attentionNotifier.NotifyAttentionAsync(notification);
    }

    [RelayCommand]
    private void SelectSession(SessionPanelViewModel session)
    {
        SelectedSession = session;
    }

    /// <summary>
    /// Moves the selection to the previous session in <see cref="Sessions"/>, wrapping from the first
    /// to the last. No-op when there are no sessions; selects the only session when there is exactly
    /// one. Drives the Ctrl+Left/Ctrl+Up keyboard switch — the modifier/enable gate lives in the view.
    /// </summary>
    public void SelectPreviousSession() => _StepSelection(-1);

    /// <summary>
    /// Moves the selection to the next session in <see cref="Sessions"/>, wrapping from the last to
    /// the first. No-op when there are no sessions. Drives the Ctrl+Right/Ctrl+Down keyboard switch.
    /// </summary>
    public void SelectNextSession() => _StepSelection(1);

    private void _StepSelection(int direction)
    {
        var count = Sessions.Count;
        if (count == 0)
        {
            return;
        }

        // No current selection → land on the first (next) or last (previous) session.
        var currentIndex = SelectedSession is null ? -1 : Sessions.IndexOf(SelectedSession);
        var startIndex = currentIndex < 0 ? (direction > 0 ? -1 : 0) : currentIndex;

        var nextIndex = ((startIndex + direction) % count + count) % count;
        SelectedSession = Sessions[nextIndex];
    }

    [RelayCommand]
    private async Task CloseSessionAsync(SessionPanelViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index < 0)
        {
            return;
        }

        session.PropertyChanged -= OnSessionPropertyChanged;
        session.CloseRequested -= OnSessionCloseRequested;
        _lastStatus.Remove(session);

        Sessions.RemoveAt(index);
        await session.DisposeAsync();

        if (ReferenceEquals(SelectedSession, session))
        {
            SelectedSession = Sessions.Count == 0
                ? null
                : Sessions[Math.Min(index, Sessions.Count - 1)];
        }

        if (Sessions.Count == 0)
        {
            IsZoomed = false;
        }
    }

    /// <summary>
    /// Close affordance entry point (#11): a busy session flips its sidebar row to an inline Close/Keep
    /// prompt first, so a running turn is never killed on a single click; an idle/waiting/done session
    /// closes straight away.
    /// </summary>
    [RelayCommand]
    private async Task RequestCloseSessionAsync(SessionPanelViewModel session)
    {
        if (session.RequiresCloseConfirmation)
        {
            session.IsConfirmingClose = true;
            return;
        }

        await CloseSessionAsync(session);
    }

    /// <summary>Confirms a pending close from the inline prompt and tears the session down.</summary>
    [RelayCommand]
    private async Task ConfirmCloseSessionAsync(SessionPanelViewModel session)
    {
        session.IsConfirmingClose = false;
        await CloseSessionAsync(session);
    }

    /// <summary>Dismisses the inline close prompt, keeping the session.</summary>
    [RelayCommand]
    private void CancelCloseSession(SessionPanelViewModel session)
    {
        session.IsConfirmingClose = false;
    }

    [RelayCommand]
    private void ToggleZoom()
    {
        IsZoomed = !IsZoomed;
    }

    /// <summary>
    /// Disposes every live session on app shutdown so each child claude process is killed and releases
    /// its MCP permission-server connection — otherwise those open SSE streams keep the server (and the
    /// whole process) alive after the window closes (bug #32).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var session in Sessions.ToList())
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.CloseRequested -= OnSessionCloseRequested;
            await session.DisposeAsync();
        }

        Sessions.Clear();
        _lastStatus.Clear();
    }
}
