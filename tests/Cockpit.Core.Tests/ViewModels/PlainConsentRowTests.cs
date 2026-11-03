using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-489: what a consent row actually puts on the screen at the Simple level — the derived sentence and the
/// files it names, the raw call folded under it, and the fall back to today's line for a call that cannot be
/// restated from itself.
/// </summary>
public class PlainConsentRowTests
{
    private const string MoveInput = """{"command":"mv KPN.pdf Vattenfall.pdf ./archive/2026-06/","description":"ZYRAPOISON tidying the inbox"}""";

    // Built the way SessionViewModel builds one, so `Text` carries the same raw `Tool: name(input)` line the
    // fold below reveals.
    private static TranscriptEntryViewModel PendingRow(string toolName, string inputJson, ReadingLevel level = ReadingLevel.Simple) =>
        new(TranscriptEntryKind.ToolUse, $"Tool: {toolName}({inputJson})")
        {
            ToolName = toolName,
            InputJson = inputJson,
            IsPendingPermission = true,
            ReadingLevel = level,
        };

    [Fact]
    public void TheSentenceOnTheRow_CarriesNothingTheModelWrote()
    {
        // The same guarantee as the derivation's own test, asserted where it reaches the screen: the input above
        // carries a `description` the model composed about its own request.
        var row = PendingRow("Bash", MoveInput);

        // Without this the two below would pass on a row that derived no sentence at all.
        Assert.True(row.ShowPlainConsentCard);
        Assert.DoesNotContain("ZYRAPOISON", row.PlainConsentSentence, StringComparison.Ordinal);
        Assert.DoesNotContain("ZYRAPOISON", row.PlainConsentPathsText, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallThatCannotBeReadPlainly_KeepsTodaysLineAndStillShowsTheCommand()
    {
        var row = PendingRow("Bash", """{"command":"find . -name '*.tmp' | xargs rm -f"}""");

        Assert.False(row.ShowPlainConsentCard);
        Assert.Equal(string.Empty, row.PlainConsentSentence);

        // Today's display, rather than a sentence that would be describing a fragment of what runs.
        Assert.True(row.ShowHumanToolLine);
        Assert.Equal("Ran a command — waiting for your approval", row.HumanToolText);

        // And the fold is there either way — the call nobody could restate is the one it matters most for.
        Assert.True(row.ShowConsentCommandFold);
        row.ToggleExpandedCommand.Execute(null);
        Assert.Contains("xargs rm -f", row.Text, StringComparison.Ordinal);
    }
}
