using System.Text.Json;

namespace Cockpit.Plugin.Bitwarden;

// What the backend and the connect page say to each other over the plugin channel. Never carries a stored token.
internal static class BitwardenChannel
{
    public const string Status = "bitwarden.status";
    public const string Test = "bitwarden.test";
    public const string Connect = "bitwarden.connect";
    public const string Disconnect = "bitwarden.disconnect";
    public const string OpenSecurity = "bitwarden.open-security";
    public const string AvailabilityChanged = "bitwarden.availability";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record TokenRequest(string Token);

internal sealed record ProjectInfo(string Name, int Secrets);

// Error is a short reason ("token invalid", "bws CLI missing", "network", ...); Projects is what the token can read.
internal sealed record TestReply(bool Ok, string? Error, List<ProjectInfo> Projects);

// Availability is the host's (Available / EncryptionOff / Locked); State is NotConfigured / Connected / Error.
internal sealed record StatusReply(string Availability, string State, string? Project, string? Error);
