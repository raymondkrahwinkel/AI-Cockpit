using SlackNet;
using SlackNet.WebApi;

namespace Cockpit.Plugin.Slack.Tests;

public class SlackConnectionErrorFormatterTests
{
    [Theory]
    [InlineData("socket_mode_disabled", "Socket Mode is turned off for this Slack app.")]
    [InlineData("invalid_auth", "the token is wrong or has been revoked.")]
    [InlineData("missing_scope", "the app is missing a required scope")]
    public void KnownErrorCodes_GetAnActionableHintAndKeepTheRawCode(string errorCode, string expectedHintFragment)
    {
        var explanation = SlackConnectionErrorFormatter.Explain(new SlackException(new ErrorResponse { Error = errorCode }));

        Assert.Contains(expectedHintFragment, explanation);
        Assert.Contains(errorCode, explanation);
    }
}
