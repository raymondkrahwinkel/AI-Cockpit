namespace Cockpit.Plugin.Slack;

// Encodes/decodes the Approve/Deny button action ids for one relayed consent prompt (AC-1025) — the prompt's
// own id round-tripped through Slack's Block Kit action id, so a click needs no server-side lookup table.
internal static class SlackConsentButtonId
{
    private const string _Prefix = "cockpit-consent";

    public static string Approve(Guid promptId) => $"{_Prefix}:{promptId:N}:approve";

    public static string Deny(Guid promptId) => $"{_Prefix}:{promptId:N}:deny";

    public static bool TryParse(string? actionId, out Guid promptId, out bool approve)
    {
        promptId = default;
        approve = false;

        var parts = actionId?.Split(':');
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
