namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `claude auth login`'s stdout, parsed line by line (AC-713). Lines verbatim from the empirical spike recorded on
// the ticket: "Opening browser to sign in…", "If the browser didn't open, visit: <url>", then the CLI blocks on
// "Paste code here if prompted >" with no trailing newline — the prompt `LooksLikeAwaitsInputPrompt` exists to
// catch, since a line-based reader alone would wait forever for a newline that never comes.
public class ClaudeLoginFlowTests
{
    [Fact]
    public void ClassifyLine_BlankLine_IsSkipped() =>
        Assert.Null(ClaudeLoginFlow.ClassifyLine("   "));

    [Fact]
    public void ClassifyLine_PlainLine_HasNoLink()
    {
        var step = ClaudeLoginFlow.ClassifyLine("Opening browser to sign in…");

        Assert.NotNull(step);
        Assert.Equal("Opening browser to sign in…", step.Message);
        Assert.Null(step.LinkToOpen);
        Assert.False(step.AwaitsInput);
    }

    [Fact]
    public void ClassifyLine_LineNamingAUrl_CarriesItAsTheLink()
    {
        const string line = "If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&client_id=9d1c250a-e61b";
        var step = ClaudeLoginFlow.ClassifyLine(line);

        Assert.NotNull(step);
        Assert.Equal(line, step.Message);
        Assert.Equal("https://claude.com/cai/oauth/authorize?code=true&client_id=9d1c250a-e61b", step.LinkToOpen?.AbsoluteUri);
        Assert.False(step.AwaitsInput);
    }

    [Fact]
    public void ClassifyLine_TrimsSurroundingWhitespaceAndNewlines() =>
        Assert.Equal("Paste code here if prompted >", ClaudeLoginFlow.ClassifyLine("  Paste code here if prompted >  \r\n")?.Message);

    [Theory]
    [InlineData("Paste code here if prompted > ")]
    [InlineData("paste code here if prompted >")]
    public void LooksLikeAwaitsInputPrompt_MatchesTheCliesActualPrompt(string pending) =>
        Assert.True(ClaudeLoginFlow.LooksLikeAwaitsInputPrompt(pending));

    [Theory]
    [InlineData("Opening browser to sign in…")]
    [InlineData("If the browser didn't open, visit: https://claude.com/cai/oauth/authorize")]
    [InlineData("")]
    public void LooksLikeAwaitsInputPrompt_DoesNotMatchOrdinaryOutput(string pending) =>
        Assert.False(ClaudeLoginFlow.LooksLikeAwaitsInputPrompt(pending));
}
