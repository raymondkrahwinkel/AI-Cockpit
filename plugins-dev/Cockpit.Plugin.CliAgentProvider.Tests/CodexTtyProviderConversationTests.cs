using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CodexTtyProvider"/>'s conversation-id reporting (AC-408): whether Codex's own thread id can be read
/// back reliably off disk has not been investigated, so this TTY route reports
/// <see cref="PluginConversationId.Unsupported"/> honestly rather than guessing at a format.
/// </summary>
public class CodexTtyProviderConversationTests
{
    [Fact]
    public void BuildLaunch_ReportsUnsupported()
    {
        var provider = new CodexTtyProvider();
        PluginConversationId? reported = null;
        var context = new PluginTtyLaunchContext("{}", new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>())
        {
            ReportConversationId = conversation => reported = conversation,
        };

        provider.BuildLaunch(context);

        Assert.Equal(PluginConversationId.Unsupported, reported);
    }
}
