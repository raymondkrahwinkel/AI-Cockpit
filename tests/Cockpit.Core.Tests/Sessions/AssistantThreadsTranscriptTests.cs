using System.Text.Json;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

// Assistant threads: a row's projects survive the log, a log from before them reads as General, and the rest of a turn
// follows the projects of the prompt or message that opened it.
public class AssistantThreadsTranscriptTests : IDisposable
{
    private const string Pane = "cockpit-assistant";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    public AssistantThreadsTranscriptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SessionTranscriptLog _Log() => new(_root, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);

    [Fact]
    public async Task ProjectsPartsAndSender_SurviveTheLog_WithTheSourceWrittenAsAName()
    {
        var log = _Log();
        var row = new TranscriptSnapshotEntry("a", "Inbox", "PR #88 is green", null, null, null, null, false, DateTimeOffset.Now)
        {
            ProjectIds = ["depot", "waymark"],
            ProjectSources = new Dictionary<string, ProjectSource> { ["depot"] = ProjectSource.Session, ["waymark"] = ProjectSource.Explicit },
            ProjectParts = [new TranscriptProjectPart("depot", 0, 6)],
            TurnProjectIds = ["depot"],
            SenderPaneId = "pane-7",
            SenderTitle = "DEP-422",
            MessageKind = "heads-up",
            SubjectPaneId = "pane-8",
        };

        await log.AppendAsync(Pane, row);

        var loaded = Assert.Single((await log.TryLoadAsync(Pane))!);
        Assert.Equal(["depot", "waymark"], loaded.ProjectIds);
        Assert.Equal(ProjectSource.Explicit, loaded.ProjectSources!["waymark"]);
        Assert.Equal(new TranscriptProjectPart("depot", 0, 6), Assert.Single(loaded.ProjectParts!));
        Assert.Equal(["depot"], loaded.TurnProjectIds);
        Assert.Equal(("pane-7", "DEP-422", "heads-up", "pane-8"), (loaded.SenderPaneId, loaded.SenderTitle, loaded.MessageKind, loaded.SubjectPaneId));
        Assert.Contains("\"Session\"", File.ReadAllText(log.LogPath(Pane)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowWithoutProjects_WritesNoneOfTheNewMembers()
    {
        var log = _Log();
        await log.AppendAsync(Pane, new TranscriptSnapshotEntry("a", "UserText", "hi", null, null, null, null, false, DateTimeOffset.Now));

        var members = JsonDocument.Parse(File.ReadLines(log.LogPath(Pane)).Last()).RootElement.EnumerateObject().Select(member => member.Name);
        Assert.DoesNotContain(members, name => name.StartsWith("Project", StringComparison.Ordinal) || name.StartsWith("Sender", StringComparison.Ordinal));
    }

    // The log main wrote before threads existed: every row comes back, none with projects, and all of them in General.
    [Fact]
    public async Task ALogFromBeforeThreads_RestoresEveryRowIntoGeneral()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", "ac1377-main-format.jsonl"), Path.Combine(_root, "old.jsonl"));
        var recorded = await _Log().TryLoadAsync("old");
        Assert.NotNull(recorded);

        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        foreach (var row in TranscriptSnapshot.Restore(recorded))
        {
            vm.Transcript.Add(row);
        }

        Assert.NotEmpty(vm.Transcript);
        Assert.All(vm.Transcript, row => Assert.False(row.HasOwnProjects));
        Assert.All(vm.Transcript, row => Assert.True(row.IsGeneral));
        vm.ShownThread = "depot";
        Assert.Empty(vm.VisibleTranscript);
        vm.ShownThread = SessionViewModel.GeneralThread;
        Assert.Equal(vm.Transcript.Count(row => row.IsRowVisible), vm.VisibleTranscript.Count);
    }

    [Fact]
    public void CaptureAndRestore_KeepTheRowsOwnProjects()
    {
        var row = new TranscriptEntryViewModel(TranscriptEntryKind.Status, "merged")
        {
            ProjectIds = ["depot"],
            ProjectSources = new Dictionary<string, ProjectSource> { ["depot"] = ProjectSource.Session },
            SenderPaneId = "pane-7",
            MessageKind = "merged",
        };

        var restored = Assert.Single(TranscriptSnapshot.Restore([TranscriptSnapshot.Capture(row)]));

        Assert.Equal(TranscriptEntryKind.Status, restored.Kind);
        Assert.Equal(["depot"], restored.ProjectIds);
        Assert.Equal(ProjectSource.Session, restored.SourceFor("depot"));
        Assert.Equal("merged", restored.MessageKind);
    }

    [Fact]
    public void TheRestOfATurn_FollowsItsPrompt_AndARowMovedOnItsOwnStaysPut()
    {
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        var prompt = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "hoe staat Depot ervoor?") { ProjectIds = ["depot"] };
        var reply = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Groen.");
        var later = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Nog iets.");
        vm.Transcript.Add(prompt);
        vm.Transcript.Add(reply);
        vm.Transcript.Add(later);

        Assert.Equal(["depot"], reply.ThreadProjectIds);
        Assert.Equal(ProjectSource.Inherited, reply.ThreadProjectSource);

        later.ProjectIds = ["waymark"];
        prompt.ProjectIds = ["cockpit"];

        Assert.Equal(["cockpit"], reply.ThreadProjectIds);
        Assert.Equal(["waymark"], later.ThreadProjectIds);

        vm.ShownThread = "cockpit";
        Assert.Equal([prompt, reply], vm.VisibleTranscript);
    }

    [Fact]
    public void SetThreadOnThePrompt_RefilesItsTurn_WithoutMovingThePrompt()
    {
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        var prompt = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "voor Depot dit, voor WayMark dat");
        var reply = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "…");
        vm.Transcript.Add(prompt);
        vm.Transcript.Add(reply);

        prompt.TurnProjectIds = ["depot", "waymark"];

        Assert.True(prompt.IsGeneral);
        Assert.Equal(["depot", "waymark"], reply.ThreadProjectIds);
    }

    // § 9.3 (a): one reply, a block per project. Text before the first marker is the whole turn's; a block runs on over
    // the rows the reply streamed into; an id no project has is General; the marker itself is never drawn or read.
    [Fact]
    public void AReplyInBlocks_FilesEachBlockUnderItsProject()
    {
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        vm.Threads = new ThreadLookup(id => id is "depot" or "waymark" ? id : null, _ => null, _ => null);
        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "voor Depot dit, voor WayMark dat")
        {
            TurnProjectIds = ["depot", "waymark"],
        });
        var intro = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Twee dingen.");
        var depot = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "<!-- project:depot -->\nDe route staat.") { IsReplyContinuation = true };
        var depotMore = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Nog een alinea.") { IsReplyContinuation = true };
        var unknown = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "<!-- project:elders -->\nIets anders.") { IsReplyContinuation = true };
        foreach (var row in new[] { intro, depot, depotMore, unknown })
        {
            vm.Transcript.Add(row);
        }

        Assert.Equal(["depot", "waymark"], intro.ThreadProjectIds);
        Assert.Equal(["depot"], depot.ThreadProjectIds);
        Assert.True(depot.StartsProjectBlock);
        Assert.Equal(["depot"], depotMore.ThreadProjectIds);
        Assert.True(unknown.IsGeneral);

        var blocks = Cockpit.Core.Markdown.MarkdownParser.Parse(depot.Text);
        Assert.Single(blocks);
        Assert.DoesNotContain(Cockpit.Core.Voice.TtsProseExtractor.Extract(depot.Text), sentence => sentence.Contains("project", StringComparison.Ordinal));
    }

    // § 9.6: the grip sits on a reply's last row, so moving it moves the whole reply, and Undo takes all of it back.
    [Fact]
    public void MovingAReply_MovesEveryRowOfIt_AndUndoPutsThemAllBack()
    {
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "hoe staat Depot ervoor?") { ProjectIds = ["depot"] });
        var first = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Groen.");
        var tail = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Verder niets.") { IsReplyContinuation = true };
        IReadOnlyList<TranscriptEntryViewModel> reply = [first, tail];
        first.ReplyRows = reply;
        tail.ReplyRows = reply;
        vm.Transcript.Add(first);
        vm.Transcript.Add(tail);

        var move = vm.MoveToThread(tail, "waymark", add: false);

        Assert.Equal(["waymark"], first.ThreadProjectIds);
        Assert.Equal(["waymark"], tail.ThreadProjectIds);

        vm.UndoMove(move);
        Assert.Null(first.ProjectIds);
        Assert.Equal(["depot"], tail.ThreadProjectIds);
        Assert.Equal(ProjectSource.Inherited, first.ThreadProjectSource);
    }

    // § 2: status lines that follow each other fold under the first, per thread — in All a Depot status between two
    // Cockpit ones is part of the run, in the Cockpit thread it is not there at all.
    [Fact]
    public void StatusLinesInARow_FoldUnderTheFirst_WorkedOutPerThread()
    {
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        TranscriptEntryViewModel Status(string project) => new(TranscriptEntryKind.Status, "merged") { ProjectIds = [project], MessageKind = "merged" };
        var first = Status("cockpit");
        var depot = Status("depot");
        var last = Status("cockpit");
        foreach (var row in new[] { first, depot, last })
        {
            vm.Transcript.Add(row);
        }

        Assert.Equal(3, first.StatusRunCount);
        Assert.Equal([first], vm.VisibleTranscript);

        first.ToggleStatusRunCommand.Execute(null);
        Assert.Equal([first, depot, last], vm.VisibleTranscript);

        vm.ShownThread = "depot";
        Assert.Equal(1, depot.StatusRunCount);
        Assert.Equal([depot], vm.VisibleTranscript);
    }

    [Fact]
    public void DeliveredMail_IsARowPerMessage_FiledUnderTheSendersProject_OrTheWatchedPanes()
    {
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        vm.Threads = new ThreadLookup(_ => null, pane => pane == "pane-8" ? "waymark" : null, pane => pane == "pane-8" ? "WM-12" : null);

        vm.NoteDeliveredMail(new AgentInboxTurnNotice(Pane,
        [
            new AgentMessage("1", "pane-7", Pane, "heads-up", "Review staat klaar", DateTimeOffset.UtcNow, SenderProjectId: "depot"),
            new AgentMessage("2", "pane-7", Pane, "merged", "PR #88 gemerged", DateTimeOffset.UtcNow, SenderProjectId: "depot"),
            new AgentMessage("3", "cockpit-session-watch", Pane, "session", "Watched session …", DateTimeOffset.UtcNow, SubjectPaneId: "pane-8"),
        ], 0));

        Assert.Equal([TranscriptEntryKind.Inbox, TranscriptEntryKind.Status, TranscriptEntryKind.Inbox], vm.Transcript.Select(row => row.Kind));
        Assert.Equal(["depot"], vm.Transcript[0].ThreadProjectIds);
        Assert.Equal(ProjectSource.Session, vm.Transcript[0].ThreadProjectSource);
        Assert.Equal(["waymark"], vm.Transcript[2].ThreadProjectIds);
        Assert.Equal("WM-12", vm.Transcript[2].SenderLabel);
    }

    [Fact]
    public void OpenQuestions_CountOnceInTotal_AndInEveryThreadTheyWaitIn()
    {
        var vm = TestSessions.Pane(Substitute.For<ISessionManager>());
        var question = new TranscriptEntryViewModel(TranscriptEntryKind.Question, "Welke?")
        {
            ProjectIds = ["depot", "waymark"],
            IsPendingBrokerAnswer = true,
        };
        vm.Transcript.Add(question);

        Assert.Equal(1, vm.OpenQuestionCount);
        Assert.Equal(1, vm.OpenQuestionCountIn("depot"));
        Assert.Equal(1, vm.OpenQuestionCountIn("waymark"));
        Assert.Equal(0, vm.OpenQuestionCountIn(SessionViewModel.GeneralThread));

        question.IsPendingBrokerAnswer = false;
        Assert.Equal(0, vm.OpenQuestionCount);
    }
}
