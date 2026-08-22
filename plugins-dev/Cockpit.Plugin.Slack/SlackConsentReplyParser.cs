using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Slack;

// Parses the text fallback for a consent prompt (AC-669 §5): "type JA/NEE" as a real, standalone second path
// next to the Approve/Deny buttons. Matched whole, ignoring case and surrounding space.
internal static class SlackConsentReplyParser
{
    public static bool TryParse(string? text, out ConsentOutcome outcome)
    {
        switch (text?.Trim().ToUpperInvariant())
        {
            case "JA":
                outcome = ConsentOutcome.Approved;
                return true;
            case "NEE":
                outcome = ConsentOutcome.Denied;
                return true;
            default:
                outcome = ConsentOutcome.Denied;
                return false;
        }
    }
}
