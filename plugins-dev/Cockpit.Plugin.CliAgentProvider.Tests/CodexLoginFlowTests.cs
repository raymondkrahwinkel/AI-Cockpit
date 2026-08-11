namespace Cockpit.Plugin.CliAgentProvider.Tests;

// `codex login --device-auth`'s stdout, parsed line by line (AC-713). Lines verbatim from the empirical spike
// recorded on the ticket: a numbered instruction naming the device-auth link, then the one-time code on its own
// line — both stream through as plain steps, the link one carrying `LinkToOpen`.
public class CodexLoginFlowTests
{
    [Fact]
    public void ClassifyLine_BlankLine_IsSkipped() =>
        Assert.Null(CodexLoginFlow.ClassifyLine(""));

    [Fact]
    public void ClassifyLine_LineNamingTheDeviceAuthLink_CarriesItAsTheLink()
    {
        var step = CodexLoginFlow.ClassifyLine("   https://auth.openai.com/codex/device  ");

        Assert.NotNull(step);
        Assert.Equal("https://auth.openai.com/codex/device", step.Message);
        Assert.Equal("https://auth.openai.com/codex/device", step.LinkToOpen?.AbsoluteUri);
        Assert.False(step.AwaitsInput);
    }

    [Fact]
    public void ClassifyLine_TheOneTimeCode_HasNoLinkAndNeverAwaitsInput()
    {
        var step = CodexLoginFlow.ClassifyLine("LVK9-G9ZP5");

        Assert.NotNull(step);
        Assert.Equal("LVK9-G9ZP5", step.Message);
        Assert.Null(step.LinkToOpen);
        Assert.False(step.AwaitsInput, "the device-auth flow polls the token endpoint itself — Cockpit never submits anything back");
    }

    [Fact]
    public void ClassifyLine_InstructionLine_HasNoLink() =>
        Assert.Null(CodexLoginFlow.ClassifyLine("1. Open this link in your browser and sign in to your account")?.LinkToOpen);
}
