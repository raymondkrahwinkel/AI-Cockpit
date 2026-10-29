using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

// AC-1937 (layer 2, persistence): a card the assistant resolved stays closed with its note after a restart, and a card
// still open comes back under the same id, so list_open_questions and resolve_structured_question work across one.
public class Ac1937_QuestionCardResolutionTests : IDisposable
{
    private const string Card = """{"questions":[{"question":"Q","multiSelect":false,"options":[{"label":"A"},{"label":"B"}]}]}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AResolvedCard_StaysClosedWithItsNote_AndAnOpenOneKeepsItsId_AcrossARestart()
    {
        var store = new SessionTranscriptLog(_root, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);
        var first = TestSessions.Pane(Substitute.For<ISessionManager>(), transcriptStore: store);
        var settled = first.AddQuestion("Which branch?", Card, []);
        var open = first.AddQuestion("Tag it?", Card, []);
        Assert.True(first.ResolveBrokerQuestion(settled.Id, AssistantQuestionResolution.AnsweredInChat, "the hotfix branch").Ok);
        await store.FlushAsync(CancellationToken.None);

        var restarted = TestSessions.Pane(Substitute.For<ISessionManager>(), transcriptStore: store);
        restarted.AdoptPaneId(first.PaneId);
        await restarted.ReplayRecordedTranscriptAsync();

        Assert.Equal([open.Id], restarted.ListOpenBrokerQuestions().Select(question => question.QuestionId));
        var replayed = restarted.Transcript.Single(row => row.Id == settled.Id);
        Assert.False(replayed.IsAwaitingAnswer);
        Assert.Equal("Answered in chat · the hotfix branch", replayed.ResolutionText);
        Assert.False(restarted.ResolveBrokerQuestion(settled.Id, AssistantQuestionResolution.Obsolete, null).Ok);
        Assert.True(restarted.ResolveBrokerQuestion(open.Id, AssistantQuestionResolution.Withdrawn, null).Ok);
    }
}
