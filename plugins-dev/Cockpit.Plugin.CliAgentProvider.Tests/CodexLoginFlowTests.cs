namespace Cockpit.Plugin.CliAgentProvider.Tests;

public class CodexLoginFlowTests
{
    // Layer 2: parse external CLI output without starting Codex.
    [Fact]
    public void ClassifyLine_StripsTerminalEscapesBeforeShowingTextAndExtractingTheDeviceLink()
    {
        var warning = CodexLoginFlow.ClassifyLine(
            "\u001b[90mContinue only if you started this login in Codex.\u001b[0m\u001b[2K");
        var device = CodexLoginFlow.ClassifyLine(
            "\u001b]0;Codex login\u0007\u001b]2;Device login\u001b\\\u001b[4mhttps://auth.openai.com/codex/device\u001b[0m");

        Assert.Equal("https://auth.openai.com/codex/device", Assert.IsType<Uri>(device?.LinkToOpen).OriginalString);
        Assert.Equal("https://auth.openai.com/codex/device", device?.Message);
        Assert.Equal("Continue only if you started this login in Codex.", warning?.Message);
        Assert.Null(warning?.LinkToOpen);
    }
}
