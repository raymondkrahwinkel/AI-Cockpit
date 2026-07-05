namespace Cockpit.Core.Notifications;

/// <summary>
/// A single "a session needs your attention" message, ready to be delivered to whichever channel
/// <see cref="NotificationRouter"/> picks. <paramref name="Title"/> is the session's panel title;
/// <paramref name="Body"/> is the human-readable reason (e.g. "Needs attention").
/// </summary>
public sealed record AttentionNotification(string Title, string Body);
