using Avalonia.Platform;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.Core.Notifications;
using Cockpit.Core.Abstractions.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

public partial class CockpitViewModel
{
    internal const string SoftConfirmationSound = "Soft confirmation";
    internal const string GentleQuestionSound = "Gentle question";
    internal const string LightPluckSound = "Light pluck";
    internal const string CustomWaveSound = "Custom file (WAV)";

    private static readonly TimeSpan NotificationSoundMinimumInterval = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, DateTimeOffset> _lastNotificationSounds = new(StringComparer.Ordinal);
    private readonly PendingNotificationSounds _pendingNotificationSounds = new((play, delay) => DispatcherTimer.RunOnce(play, delay));
    private readonly object _notificationSoundLock = new();
    private CancellationTokenSource? _notificationSoundCancellation;
    private volatile bool _isVoicePlaybackActive;

    public IReadOnlyList<string> NotificationSoundChoices { get; } =
        [SoftConfirmationSound, GentleQuestionSound, LightPluckSound, CustomWaveSound];

    [ObservableProperty]
    private bool _notificationSoundsEnabled = true;

    [ObservableProperty]
    private bool _notificationSoundOnSessionFinished = true;

    [ObservableProperty]
    private bool _notificationSoundOnNeedsAttention = true;

    [ObservableProperty]
    private string _sessionFinishedSound = SoftConfirmationSound;

    [ObservableProperty]
    private string _needsAttentionSound = GentleQuestionSound;

    [ObservableProperty]
    private string _sessionFinishedCustomSoundPath = string.Empty;

    [ObservableProperty]
    private string _needsAttentionCustomSoundPath = string.Empty;

    [ObservableProperty]
    private double _notificationSoundVolume = 0.25;

    public bool IsSessionFinishedCustomSound => SessionFinishedSound == CustomWaveSound;

    public bool IsNeedsAttentionCustomSound => NeedsAttentionSound == CustomWaveSound;

    partial void OnSessionFinishedSoundChanged(string value) => OnPropertyChanged(nameof(IsSessionFinishedCustomSound));

    partial void OnNeedsAttentionSoundChanged(string value) => OnPropertyChanged(nameof(IsNeedsAttentionCustomSound));

    [RelayCommand]
    private Task TestSessionFinishedSoundAsync() =>
        _PlaySelectedNotificationSoundAsync(SessionFinishedSound, SessionFinishedCustomSoundPath, NotificationSoundVolume);

    [RelayCommand]
    private Task TestNeedsAttentionSoundAsync() =>
        _PlaySelectedNotificationSoundAsync(NeedsAttentionSound, NeedsAttentionCustomSoundPath, NotificationSoundVolume);

    private void _NotifyWithSound(SessionPanelViewModel session, NotificationSoundEvent soundEvent)
    {
        var status = soundEvent == NotificationSoundEvent.SessionFinished ? SessionStatus.Done : SessionStatus.NeedsAttention;
        _pendingNotificationSounds.Schedule(session.PaneId, status,
            () => _SoundStillEligible(session, soundEvent),
            stillCurrent => _NotifyWithSoundAsync(session, soundEvent, stillCurrent));
    }

    private bool _SoundStillEligible(SessionPanelViewModel session, NotificationSoundEvent soundEvent) =>
        !_disposed
        && ReferenceEquals(FindSession(session.PaneId), session)
        && (soundEvent == NotificationSoundEvent.SessionFinished
            ? session.SessionStatus == SessionStatus.Done && !session.HasOutstandingBackgroundShells
            : session.SessionStatus == SessionStatus.NeedsAttention);

    private async Task _NotifyWithSoundAsync(SessionPanelViewModel session, NotificationSoundEvent soundEvent, Func<bool> stillCurrent)
    {
        try
        {
            if (!IsAudioAvailable || _notificationSettingsStore is null || _playbackService is null)
            {
                return;
            }

            var settings = await _notificationSettingsStore.LoadAsync();
            if (!stillCurrent())
            {
                return;
            }

            var eventEnabled = soundEvent == NotificationSoundEvent.SessionFinished
                ? settings.NotificationSoundOnSessionFinished
                : settings.NotificationSoundOnNeedsAttention;
            var selectedSound = soundEvent == NotificationSoundEvent.SessionFinished
                ? settings.SessionFinishedSound
                : settings.NeedsAttentionSound;
            var customPath = soundEvent == NotificationSoundEvent.SessionFinished
                ? settings.SessionFinishedCustomSoundPath
                : settings.NeedsAttentionCustomSoundPath;
            var now = DateTimeOffset.UtcNow;
            _lastNotificationSounds.TryGetValue(session.PaneId, out var lastPlayedAt);

            if (!NotificationSoundDecision.ShouldPlay(
                    settings.NotificationSoundsEnabled,
                    eventEnabled,
                    ReferenceEquals(session, SelectedSession),
                    IsWindowActive,
                    _isVoicePlaybackActive,
                    lastPlayedAt == default ? null : lastPlayedAt,
                    now,
                    NotificationSoundMinimumInterval))
            {
                return;
            }

            _lastNotificationSounds[session.PaneId] = now;
            await _PlaySelectedNotificationSoundAsync(selectedSound, customPath ?? string.Empty, settings.NotificationSoundVolume);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Notification sound for {Event} could not be played.", soundEvent);
        }
    }

    private async Task _PlaySelectedNotificationSoundAsync(string selectedSound, string customPath, double volume)
    {
        if (!IsAudioAvailable || _playbackService is null || _isVoicePlaybackActive)
        {
            return;
        }

        CancellationTokenSource playbackCancellation;
        lock (_notificationSoundLock)
        {
            _notificationSoundCancellation?.Cancel();
            playbackCancellation = new CancellationTokenSource();
            _notificationSoundCancellation = playbackCancellation;
        }

        try
        {
            var useCustomFile = selectedSound == CustomWaveSound && File.Exists(customPath);
            await using var stream = useCustomFile
                ? File.OpenRead(customPath)
                : AssetLoader.Open(new Uri($"avares://Cockpit.App/Assets/NotificationSounds/{_BundledFileName(selectedSound)}"));
            var (pcm, format) = PcmWaveFile.Read(stream);
            PcmWaveFile.ApplyVolume(pcm, volume);
            await _playbackService.PlayAsync(pcm, format, playbackCancellation.Token);
        }
        catch (OperationCanceledException) when (playbackCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Notification sound {Sound} could not be played.", selectedSound);
        }
        finally
        {
            lock (_notificationSoundLock)
            {
                if (ReferenceEquals(_notificationSoundCancellation, playbackCancellation))
                {
                    _notificationSoundCancellation = null;
                }
            }

            playbackCancellation.Dispose();
        }
    }

    private void _OnVoicePlaybackActiveChanged(object? sender, bool active)
    {
        _isVoicePlaybackActive = active;
        if (active)
        {
            lock (_notificationSoundLock)
            {
                _notificationSoundCancellation?.Cancel();
            }
        }
    }

    private static string _BundledFileName(string selectedSound) => selectedSound switch
    {
        GentleQuestionSound => "gentle-question.wav",
        LightPluckSound => "light-pluck.wav",
        _ => "soft-confirmation.wav",
    };

    private enum NotificationSoundEvent
    {
        SessionFinished,
        NeedsAttention,
    }
}
