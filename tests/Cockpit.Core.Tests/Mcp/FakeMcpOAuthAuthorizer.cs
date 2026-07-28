using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using ModelContextProtocol.Authentication;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Stands in for the real authorizer so a test can decide how far the sign-in got (AC-457). Reaching
/// <see cref="McpSignInStage.BrowserRequested"/> for real means handing a URL to the desktop of the machine running
/// the suite, so the stage is recorded here instead and the connect is left to fail as it would anyway.
/// </summary>
internal sealed class FakeMcpOAuthAuthorizer : IMcpOAuthAuthorizer
{
    /// <summary>What the authorization step is to claim it reached; nothing is recorded when this is null.</summary>
    public McpSignInStage? StageReached { get; init; }

    public ClientOAuthOptions CreateOptions(McpServerConfig server, bool interactive = true, McpSignInStageRecorder? stageRecorder = null)
    {
        if (StageReached is { } stage)
        {
            stageRecorder?.Record(stage);
        }

        // A loopback redirect nothing ever calls back on: the connect these options are handed to fails at the
        // transport, which is the whole point — only the stage this recorded is under test.
        return new ClientOAuthOptions { RedirectUri = new Uri("http://127.0.0.1:1/callback") };
    }
}
