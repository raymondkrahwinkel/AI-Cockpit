namespace Cockpit.Core.Notifications;

// A single "a session needs your attention" message, ready to be delivered to whichever channel
// `NotificationRouter` picks. `Title` is the session's panel title;
// `Body` is the human-readable reason (e.g. "Needs attention").
public sealed record AttentionNotification(string Title, string Body);
