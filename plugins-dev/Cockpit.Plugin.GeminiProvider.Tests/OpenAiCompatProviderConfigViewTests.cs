namespace Cockpit.Plugin.GeminiProvider.Tests;

// AC-1345: the Timeout (seconds) field next to Base URL in the config view — a blank box keeps the SDK
// default, a positive number is accepted, and a zero or negative value is rejected before it reaches
// TryGetConfigJson's JSON. Exercises the parsing seam directly, without constructing Avalonia controls.
public class OpenAiCompatProviderConfigViewTests
{
    [Theory]
    [InlineData("", true, null)]
    [InlineData("900", true, 900)]
    [InlineData("0", false, null)]
    [InlineData("-5", false, null)]
    public void TryParseTimeoutSeconds_AcceptsBlankOrPositive_RejectsZeroOrNegative(string text, bool expectedOk, int? expectedValue)
    {
        var ok = OpenAiCompatProviderConfigView.TryParseTimeoutSeconds(text, out var timeoutSeconds);

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedValue, timeoutSeconds);
    }
}
