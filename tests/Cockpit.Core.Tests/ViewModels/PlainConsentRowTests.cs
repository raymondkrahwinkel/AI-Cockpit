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
    public void APendingMove_AsksInPlainWordsAndNamesTheFiles()
    {
        var row = PendingRow("Bash", MoveInput);

        Assert.True(row.ShowPlainConsentCard);
        Assert.Equal("Move 2 files into ./archive/2026-06", row.PlainConsentSentence);
        Assert.True(row.HasPlainConsentPaths);
        Assert.Equal($"KPN.pdf{Environment.NewLine}Vattenfall.pdf", row.PlainConsentPathsText);

        // The coarse "Ran a command — waiting for your approval" stands down for a call that speaks for itself.
        Assert.False(row.ShowHumanToolLine);
    }

    [Fact]
    public void TheSentenceOnTheRow_CarriesNothingTheModelWrote()
    {
        // The same guarantee as the derivation's own test, asserted where it reaches the screen: the input above
        // carries a `description` the model composed about its own request.
        var row = PendingRow("Bash", MoveInput);

        Assert.DoesNotContain("ZYRAPOISON", row.PlainConsentSentence, StringComparison.Ordinal);
        Assert.DoesNotContain("ZYRAPOISON", row.PlainConsentPathsText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRawCall_IsOneClickAwayAndAlwaysThere()
    {
        var row = PendingRow("Bash", MoveInput);

        Assert.True(row.ShowConsentCommandFold);
        Assert.Equal("Show the command", row.ConsentCommandToggleLabel);
        Assert.False(row.ShowConsentCommand);

        row.ToggleExpandedCommand.Execute(null);

        Assert.True(row.ShowConsentCommand);
        Assert.Equal("Hide the command", row.ConsentCommandToggleLabel);
        // What the fold reveals is the call itself, not a retelling of it.
        Assert.Contains("mv KPN.pdf Vattenfall.pdf ./archive/2026-06/", row.Text, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(ReadingLevel.Developer)]
    [InlineData(ReadingLevel.Focus)]
    [InlineData(ReadingLevel.Simple)]
    public void TheSentenceIsOnOneScreenForBothAudiences(ReadingLevel level)
    {
        var row = PendingRow("Bash", MoveInput, level);

        Assert.True(row.ShowPlainConsentCard, $"the approval reads the same at {level}");
        Assert.Equal("Move 2 files into ./archive/2026-06", row.PlainConsentSentence);
    }

    [Theory]
    [InlineData(ReadingLevel.Developer)]
    [InlineData(ReadingLevel.Focus)]
    public void AboveTheDeveloperLevel_TheToolChipStaysTheFold(ReadingLevel level)
    {
        var row = PendingRow("Bash", MoveInput, level);

        // A second "Show the command" beside the chip would say the same thing twice.
        Assert.False(row.ShowConsentCommandFold);
        Assert.True(row.ShowToolBlock);
    }

    [Fact]
    public void OnceAnswered_TheRowSaysWhichWayItWent_AndKeepsTheCommand()
    {
        var row = PendingRow("Bash", MoveInput);
        row.IsPendingPermission = false;
        row.PermissionDecision = "Allowed";

        // The sentence alone would drop the decision, which is the reason the row is still on screen at all.
        Assert.False(row.ShowPlainConsentCard);
        Assert.True(row.ShowHumanToolLine);
        Assert.Equal("✓ Ran a command — you approved this", row.HumanToolText);

        Assert.True(row.ShowConsentCommandFold);
    }

    [Fact]
    public void AnAnsweredQuestion_KeepsItsOwnCard()
    {
        // AC-715's question rides the same permission callback but asks for an answer, not consent — Allow/Deny
        // is not a reply to it, and neither is a sentence about moving files.
        var row = PendingRow("AskUserQuestion", """{"questions":[{"question":"Which month?","options":[{"label":"June"}]}]}""");
        row.QuestionPrompts = AskUserQuestionViewModel.Parse(row.InputJson);

        Assert.False(row.ShowPlainConsentCard);
        Assert.False(row.ShowConsentCommandFold);
        Assert.False(row.ShowHumanToolLine);
    }
}
