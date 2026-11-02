namespace Cockpit.Core.Notifications;

public static class NotificationSoundDecision
{
    public static bool ShouldPlay(
        bool soundsEnabled,
        bool eventEnabled,
        bool isSelected,
        bool isWindowActive,
        bool isVoicePlaybackActive,
        DateTimeOffset? lastPlayedAt,
        DateTimeOffset now,
        TimeSpan minimumInterval) =>
        soundsEnabled
        && eventEnabled
        && !(isSelected && isWindowActive)
        && !isVoicePlaybackActive
        && (lastPlayedAt is null || now - lastPlayedAt >= minimumInterval);
}
