namespace Cockpit.Plugin.Discord;

// Encodes/decodes the Approve/Deny button custom ids for one relayed consent prompt (AC-1024) — the prompt's
// own id round-tripped through Discord's Components API, so a click needs no server-side lookup table.
internal static class DiscordConsentButtonId
{
    private const string _Prefix = "cockpit-consent";

    public static string Approve(Guid promptId) => $"{_Prefix}:{promptId:N}:approve";

    public static string Deny(Guid promptId) => $"{_Prefix}:{promptId:N}:deny";

    public static bool TryParse(string? customId, out Guid promptId, out bool approve)
    {
        promptId = default;
        approve = false;

        var parts = customId?.Split(':');
        if (parts is not { Length: 3 } || parts[0] != _Prefix || !Guid.TryParseExact(parts[1], "N", out promptId))
        {
            return false;
        }

        switch (parts[2])
        {
            case "approve":
                approve = true;
                return true;
            case "deny":
                approve = false;
                return true;
            default:
                return false;
        }
    }
}
