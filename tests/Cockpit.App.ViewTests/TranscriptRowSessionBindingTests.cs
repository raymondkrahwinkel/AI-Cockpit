using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-990: a virtualised transcript row is built before it hangs in the tree, so a row that looked its session up
/// through its host found nothing and logged a binding error per realisation — 99% of every binding error the app
/// produced, and 0.4–0.7 MB of heap apiece.
/// </summary>
[Collection("avalonia")]
public class TranscriptRowSessionBindingTests
{
    private static SessionViewModel _TranscriptOf(int rows)
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < rows; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        return session;
    }

    [Fact]
    public void ASubAgentRow_InheritsTheSessionOfTheRowItHangsUnder()
    {
        var session = _TranscriptOf(1);
        var anchor = session.Transcript[0];

        anchor.SubAgentRows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "nested"));

        Assert.Same(session, anchor.SubAgentRows[0].Session);
    }
}
