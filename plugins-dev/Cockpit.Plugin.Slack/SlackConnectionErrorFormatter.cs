using SlackNet;

namespace Cockpit.Plugin.Slack;

// A handful of connection failures trace back to a switch in the Slack app config rather than a typo in a
// token; those get a one-line fix alongside the raw code. Everything else falls through to SlackNet's own
// message unchanged rather than growing a translation table for every code Slack could ever return.
internal static class SlackConnectionErrorFormatter
{
    public static string Explain(Exception exception)
    {
        if (exception is not SlackException slackException)
        {
            return exception.Message;
        }

        var hint = slackException.ErrorCode switch
        {
            "socket_mode_disabled" => "Socket Mode is turned off for this Slack app.",
            "invalid_auth" => "the token is wrong or has been revoked.",
            "missing_scope" => "the app is missing a required scope — reinstall it in the workspace after adding one.",
            _ => null,
        };

        return hint is null ? slackException.Message : $"{hint} ({slackException.ErrorCode})";
    }
}
