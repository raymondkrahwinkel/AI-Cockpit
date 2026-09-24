using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

// AC-1377: row forming moved from `SessionViewModel` to `SessionHost`. Recorded against main before the move, so the
// same stream has to give the same rows after it — live, restored from what the new code writes, and restored from
// a log main wrote (`Fixtures/ac1377-main-format.jsonl`).
public class Ac1377_TranscriptRowCharacterizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TheFixedStream_FormsTheseRows_AndRestoresThemFromWhatItWrote()
    {
        var log = _Log();
        var vm = new SessionViewModel(Substitute.For<ISessionManager>(), transcriptStore: log);

        Array.ForEach(Stream, vm.Apply);
        var ids = _Flatten(vm.Transcript).Select(row => row.Id).ToList();
        var live = _Render(vm.Transcript, ids);
        await log.DisposeAsync();
        var recorded = await _Log().TryLoadAsync(vm.PaneId);
        Assert.NotNull(recorded);
        var restored = TranscriptSnapshot.Restore(recorded);

        Assert.Equal(LiveRows.ReplaceLineEndings("\n"), live);
        Assert.Equal(RestoredRows.ReplaceLineEndings("\n"), _Render(restored, ids));
    }

    [Fact]
    public async Task ALogMainWrote_StillRestoresToTheSameRows()
    {
        Directory.CreateDirectory(_root);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", "ac1377-main-format.jsonl"), Path.Combine(_root, "main-pane.jsonl"));

        var recorded = await _Log().TryLoadAsync("main-pane");
        Assert.NotNull(recorded);
        var restored = TranscriptSnapshot.Restore(recorded);

        Assert.Equal(RestoredRows.ReplaceLineEndings("\n"), _Render(restored, _Flatten(restored).Select(row => row.Id).ToList()));
    }

    // Coalesced into one write at dispose, so the file holds one line per row whatever the timing was.
    private SessionTranscriptLog _Log() =>
        new(_root, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.FromHours(1));

    private static IEnumerable<TranscriptEntryViewModel> _Flatten(IEnumerable<TranscriptEntryViewModel> rows) =>
        rows.SelectMany(row => new[] { row }.Concat(_Flatten(row.SubAgentRowsForDisplay)));

    private static string _Render(IEnumerable<TranscriptEntryViewModel> rows, IReadOnlyList<string> ids, string indent = "") =>
        string.Join('\n', rows.Select(row => _Line(row, ids, indent)
            + (row.HasSubAgentRows ? "\n" + _Render(row.SubAgentRowsForDisplay, ids, indent + "  ") : string.Empty)));

    // Ids are Guids, so a row names its position in the live transcript instead; a lost or changed id reads -1.
    private static string _Line(TranscriptEntryViewModel row, IReadOnlyList<string> ids, string indent) =>
        $"{indent}#{_Index(ids, row.Id)} {row.Kind} «{_Show(row.Text)}»"
        + $" tool={row.ToolName}/{row.ToolUseId}/{_Show(row.InputJson)} result={_Show(row.ResultText)}/{row.IsResultError}/{row.TruncatedFromChars}"
        + $" permission={row.PermissionDecision}/{row.IsPendingPermission}/{row.QuestionPrompts?.Count}"
        + $" bg={row.BackgroundTaskId} error={row.ErrorKind}/{row.IsFailedTurnRow} expanded={row.IsExpanded}"
        + $" reply={row.IsReplyContinuation}/{row.IsReplyTail}/{_Group(row.ReplyRows, ids)}"
        + $" code={row.StartsInsideCodeBlock}/{row.EndsInsideCodeBlock}/{_Group(row.CodeSpanRows, ids)}"
        + $" table={row.StartsInsideTable}/{row.EndsInsideTable}/{_Group(row.TableSpanRows, ids)}/{row.TableSpanRevision}";

    private static int _Index(IReadOnlyList<string> ids, string id) => ids.ToList().IndexOf(id);

    private static string _Group(IReadOnlyList<TranscriptEntryViewModel>? group, IReadOnlyList<string> ids) =>
        group is null ? "-" : string.Join(',', group.Select(row => _Index(ids, row.Id)));

    // Newlines spelled out so a row is one line; a clamped value by its ends and length, its marker being locale-formatted.
    private static string _Show(string? text) => text is null
        ? string.Empty
        : text.Length <= 400
            ? text.Replace("\n", "\\n", StringComparison.Ordinal)
            : $"{_Show(text[..30])}…{_Show(text[^30..])} [{text.Length}]";

    private const string Code =
        "line 01\nline 02\nline 03\nline 04\nline 05\nline 06\nline 07\nline 08\nline 09\nline 10\nline 11\nline 12\n";

    private const string MoreCode =
        "line 13\nline 14\nline 15\nline 16\nline 17\nline 18\nline 19\nline 20\nline 21\nline 22\nline 23\nline 24\nline 25\n";

    private const string QuestionInput =
        "{\"questions\":[{\"question\":\"Pick one?\",\"header\":\"Pick\",\"options\":[{\"label\":\"A\",\"description\":\"a\"},{\"label\":\"B\",\"description\":\"b\"}]}]}";

    private static readonly string Lorem = string.Join(' ', Enumerable.Repeat("lorem", 130));

    // Every edge the fold has: blank-line splits, a fence and a table over their row bounds, an unbroken paragraph,
    // two thinking blocks, coupled and uncoupled tool results, known and orphan permissions, a question card, a
    // sub-agent lane, an orphan sub-agent, a completed-after-deltas and a completed-alone, a failed turn, an error.
    private static readonly SessionEvent[] Stream =
    [
        new AssistantThinkingDelta { SessionId = "S", BlockIndex = 0, Thinking = "Let me look" },
        new AssistantThinkingDelta { SessionId = "S", BlockIndex = 0, Thinking = " at it." },
        new AssistantThinkingDelta { SessionId = "S", BlockIndex = 1, Thinking = "Second thought." },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = "Intro paragraph.\n\nSecond " },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = "paragraph.\n\nHere is code:\n\n" },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = "```csharp\n" + Code },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = MoreCode + "```\n" },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = "\nAfter code.\n\n| a | b |\n|---|---|\n| 1 | 2 |\n" },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = "| 3 | 4 |\n| 5 | 6 |\n| 7 | 8 |\n| 9 | 0 |\n" },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = "\nDone with the table.\n\n" + Lorem[..400] },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = Lorem[400..] },
        new ToolUseRequested { SessionId = "S", ToolUseId = "t1", ToolName = "Bash", InputJson = "{\"command\":\"ls\"}" },
        new ToolResult { SessionId = "S", ToolUseId = "t1", Content = "a.txt\nb.txt", IsError = false },
        new ToolUseRequested { SessionId = "S", ToolUseId = "t2", ToolName = "Bash", InputJson = "{\"command\":\"rm a.txt\"}" },
        new PermissionRequested { SessionId = "S", ToolUseId = "t2", ToolName = "Bash", InputJson = "{\"command\":\"rm a.txt\"}" },
        new PermissionRequested { SessionId = "S", ToolUseId = "t3", ToolName = "Write", InputJson = "{\"file_path\":\"x\"}" },
        new ToolUseRequested { SessionId = "S", ToolUseId = "q1", ToolName = "AskUserQuestion", InputJson = QuestionInput },
        new PermissionRequested { SessionId = "S", ToolUseId = "q1", ToolName = "AskUserQuestion", InputJson = QuestionInput },
        new ToolUseRequested { SessionId = "S", ToolUseId = "t4", ToolName = "Bash", InputJson = "{\"command\":\"serve\",\"run_in_background\":true}" },
        new ToolResult { SessionId = "S", ToolUseId = "t4", Content = "Command running in background with ID: bg7", IsError = false },
        new ToolUseRequested { SessionId = "S", ToolUseId = "t5", ToolName = "Read", InputJson = "{\"file_path\":\"big\"}" },
        new ToolResult { SessionId = "S", ToolUseId = "t5", Content = new string('x', ToolOutputBudget.MaxChars + 500), IsError = true },
        new ToolUseRequested { SessionId = "S", ToolUseId = "task", ToolName = "Task", InputJson = "{\"prompt\":\"dig\"}" },
        new AssistantThinkingDelta { SessionId = "S", ParentToolUseId = "task", BlockIndex = 0, Thinking = "sub thinks" },
        new AssistantTextDelta { SessionId = "S", ParentToolUseId = "task", BlockIndex = 0, Text = "sub says " },
        new AssistantTextDelta { SessionId = "S", ParentToolUseId = "task", BlockIndex = 0, Text = "hi" },
        new ToolUseRequested { SessionId = "S", ParentToolUseId = "task", ToolUseId = "s1", ToolName = "Read", InputJson = "{}" },
        new ToolResult { SessionId = "S", ParentToolUseId = "task", ToolUseId = "s1", Content = "sub result", IsError = false },
        new ToolUseRequested { SessionId = "S", ParentToolUseId = "task", ToolUseId = "s2", ToolName = "Edit", InputJson = "{}" },
        new PermissionRequested { SessionId = "S", ParentToolUseId = "task", ToolUseId = "s2", ToolName = "Edit", InputJson = "{}" },
        new ToolResult { SessionId = "S", ParentToolUseId = "task", ToolUseId = "s9", Content = "sub orphan result", IsError = false },
        new AssistantTextCompleted { SessionId = "S", ParentToolUseId = "task", Text = "sub done" },
        new ToolResult { SessionId = "S", ToolUseId = "task", Content = "dug", IsError = false },
        new AssistantTextDelta { SessionId = "S", ParentToolUseId = "gone", BlockIndex = 0, Text = "orphan text" },
        new AssistantTextCompleted { SessionId = "S", ParentToolUseId = "gone", Text = "orphan text" },
        new AssistantTextCompleted { SessionId = "S", ParentToolUseId = "gone", Text = "orphan alone" },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = "Final words." },
        new AssistantTextCompleted { SessionId = "S", Text = "Final words." },
        new AssistantTextCompleted { SessionId = "S", Text = "Standalone." },
        new Question { SessionId = "S", Text = "Which one?" },
        new ToolResult { SessionId = "S", ToolUseId = "ghost", Content = "ghost result", IsError = false },
        new TurnCompleted { SessionId = "S", Subtype = "error_during_execution", Result = null, IsError = true, Errors = ["boom"] },
        new AssistantTextDelta { SessionId = "S", BlockIndex = 0, Text = "Next turn.\n\nTail" },
        new SessionError { SessionId = "S", Message = "driver died" },
    ];

    private const string LiveRows = """
    #0 Thinking «Let me look at it.» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=True reply=False/True/- code=False/False/- table=False/False/-/0
    #1 Thinking «Second thought.» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=True reply=False/True/- code=False/False/- table=False/False/-/0
    #2 AssistantText «Intro paragraph.\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/False/2,3,4,5,6,7,8,9 code=False/False/- table=False/False/-/0
    #3 AssistantText «Second paragraph.\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=True/False/2,3,4,5,6,7,8,9 code=False/False/- table=False/False/-/0
    #4 AssistantText «Here is code:\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=True/False/2,3,4,5,6,7,8,9 code=False/False/- table=False/False/-/0
    #5 AssistantText «```csharp\nline 01\nline 02\nline 03\nline 04\nline 05\nline 06\nline 07\nline 08\nline 09\nline 10\nline 11\nline 12\nline 13\nline 14\nline 15\nline 16\nline 17\nline 18\nline 19\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=True/False/2,3,4,5,6,7,8,9 code=False/True/5,6 table=False/False/-/0
    #6 AssistantText «line 20\nline 21\nline 22\nline 23\nline 24\nline 25\n```\n\nAfter code.\n\n| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=True/False/2,3,4,5,6,7,8,9 code=True/False/5,6 table=False/True/6,7/1
    #7 AssistantText «| 5 | 6 |\n| 7 | 8 |\n| 9 | 0 |\n\nDone with the table.\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=True/False/2,3,4,5,6,7,8,9 code=False/False/- table=True/False/6,7/1
    #8 AssistantText «lorem lorem lorem lorem lorem …lorem lorem lorem lorem lorem  [606]» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=True/False/2,3,4,5,6,7,8,9 code=False/False/- table=False/False/-/0
    #9 AssistantText «lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=True/True/2,3,4,5,6,7,8,9 code=False/False/- table=False/False/-/0
    #10 ToolUse «Tool: Bash({"command":"ls"})» tool=Bash/t1/{"command":"ls"} result=a.txt\nb.txt/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #11 ToolUse «Tool: Bash({"command":"rm a.txt"})» tool=Bash/t2/{"command":"rm a.txt"} result=/False/0 permission=/True/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #12 ToolUse «Tool: Write({"file_path":"x"})» tool=Write/t3/{"file_path":"x"} result=/False/0 permission=/True/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #13 ToolUse «Tool: AskUserQuestion({"questions":[{"question":"Pick one?","header":"Pick","options":[{"label":"A","description":"a"},{"label":"B","description":"b"}]}]})» tool=AskUserQuestion/q1/{"questions":[{"question":"Pick one?","header":"Pick","options":[{"label":"A","description":"a"},{"label":"B","description":"b"}]}]} result=/False/0 permission=/True/1 bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #14 ToolUse «Tool: Bash({"command":"serve","run_in_background":true})» tool=Bash/t4/{"command":"serve","run_in_background":true} result=Command running in background with ID: bg7/False/0 permission=/False/ bg=bg7 error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #15 ToolUse «Tool: Read({"file_path":"big"})» tool=Read/t5/{"file_path":"big"} result=xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx…xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx [65596]/True/66036 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #16 ToolUse «Tool: Task({"prompt":"dig"})» tool=Task/task/{"prompt":"dig"} result=dug/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #17 Thinking «sub thinks» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=True reply=False/True/- code=False/False/- table=False/False/-/0
      #18 AssistantText «sub says hi» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #19 ToolUse «Tool: Read({})» tool=Read/s1/{} result=sub result/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #20 ToolUse «Tool: Edit({})» tool=Edit/s2/{} result=/False/0 permission=/True/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #21 ToolResult «sub orphan result» tool=/s9/ result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #22 AssistantText «sub done» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #23 AssistantText «orphan text» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #24 AssistantText «orphan alone» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #25 AssistantText «Final words.» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/25 code=False/False/- table=False/False/-/0
    #26 AssistantText «Standalone.» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #27 Question «Which one?» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #28 ToolResult «ghost result» tool=/ghost/ result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #29 TurnCompleted «Turn failed (error_during_execution): boom» tool=// result=/False/0 permission=/False/ bg= error=Unknown/True expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #30 AssistantText «Next turn.\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/False/30,31 code=False/False/- table=False/False/-/0
    #31 AssistantText «Tail» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=True/True/30,31 code=False/False/- table=False/False/-/0
    #32 Error «driver died» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    """;

    private const string RestoredRows = """
    #0 Thinking «Let me look at it.» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #1 Thinking «Second thought.» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #2 AssistantText «Intro paragraph.\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #3 AssistantText «Second paragraph.\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #4 AssistantText «Here is code:\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #5 AssistantText «```csharp\nline 01\nline 02\nline 03\nline 04\nline 05\nline 06\nline 07\nline 08\nline 09\nline 10\nline 11\nline 12\nline 13\nline 14\nline 15\nline 16\nline 17\nline 18\nline 19\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #6 AssistantText «line 20\nline 21\nline 22\nline 23\nline 24\nline 25\n```\n\nAfter code.\n\n| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #7 AssistantText «| 5 | 6 |\n| 7 | 8 |\n| 9 | 0 |\n\nDone with the table.\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #8 AssistantText «lorem lorem lorem lorem lorem …lorem lorem lorem lorem lorem  [606]» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #9 AssistantText «lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem lorem» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #10 ToolUse «Tool: Bash({"command":"ls"})» tool=Bash/t1/{"command":"ls"} result=a.txt\nb.txt/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #11 ToolUse «Tool: Bash({"command":"rm a.txt"})» tool=Bash/t2/{"command":"rm a.txt"} result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #12 ToolUse «Tool: Write({"file_path":"x"})» tool=Write/t3/{"file_path":"x"} result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #13 ToolUse «Tool: AskUserQuestion({"questions":[{"question":"Pick one?","header":"Pick","options":[{"label":"A","description":"a"},{"label":"B","description":"b"}]}]})» tool=AskUserQuestion/q1/{"questions":[{"question":"Pick one?","header":"Pick","options":[{"label":"A","description":"a"},{"label":"B","description":"b"}]}]} result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #14 ToolUse «Tool: Bash({"command":"serve","run_in_background":true})» tool=Bash/t4/{"command":"serve","run_in_background":true} result=Command running in background with ID: bg7/False/0 permission=/False/ bg=bg7 error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #15 ToolUse «Tool: Read({"file_path":"big"})» tool=Read/t5/{"file_path":"big"} result=xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx…xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx [65595]/True/65596 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #16 ToolUse «Tool: Task({"prompt":"dig"})» tool=Task/task/{"prompt":"dig"} result=dug/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #17 Thinking «sub thinks» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #18 AssistantText «sub says hi» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #19 ToolUse «Tool: Read({})» tool=Read/s1/{} result=sub result/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #20 ToolUse «Tool: Edit({})» tool=Edit/s2/{} result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #21 ToolResult «sub orphan result» tool=/s9/ result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
      #22 AssistantText «sub done» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #23 AssistantText «orphan text» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #24 AssistantText «orphan alone» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #25 AssistantText «Final words.» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #26 AssistantText «Standalone.» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #27 Question «Which one?» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #28 ToolResult «ghost result» tool=/ghost/ result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #29 TurnCompleted «Turn failed (error_during_execution): boom» tool=// result=/False/0 permission=/False/ bg= error=Unknown/True expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #30 AssistantText «Next turn.\n\n» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #31 AssistantText «Tail» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    #32 Error «driver died» tool=// result=/False/0 permission=/False/ bg= error=Unknown/False expanded=False reply=False/True/- code=False/False/- table=False/False/-/0
    """;
}
