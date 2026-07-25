namespace Cockpit.Plugins.Abstractions.Tracking;

/// <summary>
/// A comment read back from a tracker (AC-155) — who wrote it, its text, and when — normalized across trackers so a
/// consumer (Autopilot's blockade channel) can watch an issue for the operator's reply without knowing which tracker.
/// </summary>
public sealed record TrackerComment(string AuthorLogin, string Text, DateTimeOffset Timestamp);
