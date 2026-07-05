using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

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
public partial class CockpitViewModel : ViewModelBase, ITransientService
{
    private static readonly Core.Audio.AudioFormat AudioFormat = new();

    private readonly Func<ClaudeSessionViewModel>? _sessionFactory;
    private readonly Func<ClaudeTtyViewModel>? _ttySessionFactory;
    private readonly IAudioCaptureService? _captureService;
    private readonly IAudioPlaybackService? _playbackService;
    private readonly IAttentionNotifier? _attentionNotifier;
    private readonly INotificationSettingsStore? _notificationSettingsStore;
    private readonly List<byte> _recordedPcm = [];

    // Last observed status per session, so a NeedsAttention notification fires only on the edge into
    // that state — not on every property change while a session already needs attention.
    private readonly Dictionary<SessionPanelViewModel, SessionStatus> _lastStatus = [];
    private CancellationTokenSource? _recordingCancellation;
    private int _sessionCounter;

    public ObservableCollection<SessionPanelViewModel> Sessions { get; } = [];

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
        var waiting = new ClaudeSessionViewModel { Title = "Claude 1", ActiveProfileLabel = "werk", SessionStatus = SessionStatus.NeedsAttention };
        var busy = new ClaudeSessionViewModel { Title = "Claude 2", ActiveProfileLabel = "privé", SessionStatus = SessionStatus.Busy };
        var tty = new ClaudeTtyViewModel { Title = "Claude 3", ActiveProfileLabel = "werk (TTY)", SessionStatus = SessionStatus.Busy };

        Sessions.Add(waiting);
        Sessions.Add(busy);
        Sessions.Add(tty);
        _sessionCounter = Sessions.Count;
        SelectedSession = waiting;
    }

    public CockpitViewModel(
        Func<ClaudeSessionViewModel> sessionFactory,
        Func<ClaudeTtyViewModel> ttySessionFactory,
        IAudioCaptureService captureService,
        IAudioPlaybackService playbackService,
        IAttentionNotifier attentionNotifier,
        INotificationSettingsStore notificationSettingsStore)
    {
        _sessionFactory = sessionFactory;
        _ttySessionFactory = ttySessionFactory;
        _captureService = captureService;
        _playbackService = playbackService;
        _attentionNotifier = attentionNotifier;
        _notificationSettingsStore = notificationSettingsStore;
        NewSession();
        _ = LoadNotificationSettingsAsync();
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

    /// <summary>Starts a default SDK-mode session (headless stream-json rendered as the chat UI).</summary>
    [RelayCommand]
    private void NewSession()
    {
        if (_sessionFactory is null)
        {
            return;
        }

        AddSession(_sessionFactory());
    }

    /// <summary>Starts a TTY-mode session (the real interactive <c>claude</c> TUI in a terminal panel) — the #9 experiment.</summary>
    [RelayCommand]
    private void NewTtySession()
    {
        if (_ttySessionFactory is null)
        {
            return;
        }

        AddSession(_ttySessionFactory());
    }

    private void AddSession(SessionPanelViewModel session)
    {
        _sessionCounter++;
        session.Title = $"Claude {_sessionCounter}";

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

    [RelayCommand]
    private async Task CloseSessionAsync(SessionPanelViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index < 0)
        {
            return;
        }

        session.PropertyChanged -= OnSessionPropertyChanged;
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

    [RelayCommand]
    private void ToggleZoom()
    {
        IsZoomed = !IsZoomed;
    }
}
