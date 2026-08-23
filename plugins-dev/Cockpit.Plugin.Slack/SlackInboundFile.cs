namespace Cockpit.Plugin.Slack;

// One file hanging off an inbound Slack message (AC-1049), flattened out of SlackNet's own model so
// `SlackChannelBridge` keeps being testable without a socket. Everything here is what Slack *said* about the
// file — a hint worth a cheap pre-filter, never the thing that decides an image is one.
internal sealed record SlackInboundFile(string? Name, string? MimeType, long Size, string? Url);
