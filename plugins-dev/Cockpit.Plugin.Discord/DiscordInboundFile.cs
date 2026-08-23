namespace Cockpit.Plugin.Discord;

// One attachment on an inbound Discord message (AC-1049), flattened out of Discord.NET's own model so
// `DiscordChannelBridge` keeps being testable without a socket. Everything here is what Discord *said* about
// the file — a hint worth a cheap pre-filter, never the thing that decides an image is one.
internal sealed record DiscordInboundFile(string? Name, string? MimeType, long Size, string? Url);
