using System.Text.Json;

namespace Cockpit.Plugin.OnePassword;

// What the backend and the connect page say to each other over the plugin channel. Never carries a stored token.
internal static class OnePasswordChannel
{
    public const string Status = "onepassword.status";
    public const string PrepareCli = "onepassword.prepare-cli";
    public const string Test = "onepassword.test";
    public const string Connect = "onepassword.connect";
    public const string Disconnect = "onepassword.disconnect";
    public const string OpenSecurity = "onepassword.open-security";
    public const string AvailabilityChanged = "onepassword.availability";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record TokenRequest(string Token);

internal sealed record VaultInfo(string Name, int Items);

// Error is a short reason ("token invalid", "op CLI missing", "network", ...); Vaults is what the token can read.
internal sealed record TestReply(bool Ok, string? Error, List<VaultInfo> Vaults);

// Availability is the host's (Available / EncryptionOff / Locked); State is NotConfigured / Connected / Error.
internal sealed record StatusReply(string Availability, string State, string? Vault, string? Error);
