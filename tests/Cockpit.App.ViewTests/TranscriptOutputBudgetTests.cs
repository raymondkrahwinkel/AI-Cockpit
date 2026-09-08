using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1088: the host keeps head and tail of a large tool output, not the whole of it. Every place that used to
/// hold one entire — a result coupled to its call, a standalone result row, a call's input JSON, a call's own row
/// text, a permission's input, streamed text, and the same inside a sub-agent — is capped in characters.
/// </summary>
[Collection("avalonia")]
public class TranscriptOutputBudgetTests
{
    private const string Head = "HEAD-MARKER";
    private const string Tail = "TAIL-MARKER";

    // Twice the cap, so head and tail land far enough apart that nothing but a real clamp can bring them together.
    private static readonly string Huge = Head + new string('x', ToolOutputBudget.MaxChars * 2) + Tail;

    // The clamped value plus its marker; the marker is a fixed short line, so this is the whole retained cost.
    private static readonly int Ceiling = ToolOutputBudget.MaxChars + 200;

    private static ToolUseRequested Call(string id, string inputJson, string tool = "Bash", string? parent = null) =>
        new() { SessionId = "s1", ToolUseId = id, ToolName = tool, InputJson = inputJson, ParentToolUseId = parent };

    private static ToolResult Result(string id, string content, string? parent = null) =>
        new() { SessionId = "s1", ToolUseId = id, Content = content, IsError = false, ParentToolUseId = parent };

    [Theory]
    [InlineData("a result coupled to its call")]
    [InlineData("a standalone result row")]
    [InlineData("a call's input json")]
    [InlineData("a call's own row text")]
    [InlineData("a permission request's input")]
    [InlineData("streamed assistant text")]
    [InlineData("a sub-agent's result")]
    public void EveryPlaceThatKeepsAToolOutput_IsCappedInCharacters(string place) => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        var kept = _Drive(session, place);

        Assert.True(
            kept.Length <= Ceiling,
            $"{place} kept {kept.Length:N0} characters, over the {Ceiling:N0} ceiling.");
        Assert.Contains("characters omitted", kept);
    });

    [Fact]
    public void ATruncatedResult_SaysSo_AndNamesTheSizeItReallyHad() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Call("t1", """{"command":"cat big.log"}"""));
        session.Apply(Result("t1", Huge));

        var row = session.Transcript.Single(r => r.ToolUseId == "t1" && r.Kind == TranscriptEntryKind.ToolUse);
        Assert.True(row.IsTruncated);

        // The real size, in both places it is stated: the badge next to the row, and the marker inside the text
        // itself so it survives being copied out of the app.
        Assert.Equal(Huge.Length, row.TruncatedFromChars);
        Assert.Contains($"{Huge.Length:N0} characters", row.TruncationNotice);
        Assert.Contains($"{Huge.Length:N0} in the full result", row.ResultText!);

        // And it reaches the screen: the footer is a keyed template resolved at runtime, so only rendering the
        // real row proves the size and the offer are actually on it.
        row.IsExpanded = true;
        var window = new Window { Width = 500, Height = 400, Content = new TranscriptRowView { DataContext = row } };
        window.Show();
        window.UpdateLayout();

        var rendered = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        Assert.Contains(rendered, text => text is not null && text.Contains($"{Huge.Length:N0} characters", StringComparison.Ordinal));
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Show full result");
        window.Close();
    });

    [Fact]
    public void ScrollingBack_StillFindsEveryRow_WithEverythingUnderTheCapWhole() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        var before = session.Transcript.Count;
        const string Small = "Build succeeded. 0 Warning(s)";

        for (var i = 0; i < 20; i++)
        {
            session.Apply(Call($"big{i}", """{"command":"cat big.log"}"""));
            session.Apply(Result($"big{i}", Huge));
            session.Apply(Call($"small{i}", """{"command":"dotnet build"}"""));
            session.Apply(Result($"small{i}", Small));
        }

        Assert.Equal(before + 40, session.Transcript.Count);

        // A result under the cap is not touched at all, and a clamped one still opens and closes on what it said.
        Assert.All(
            Enumerable.Range(0, 20),
            i =>
            {
                Assert.Equal(Small, _Row(session, $"small{i}").ResultText);

                var big = _Row(session, $"big{i}").ResultText!;
                Assert.StartsWith(Head, big, StringComparison.Ordinal);
                Assert.EndsWith(Tail, big, StringComparison.Ordinal);
            });
    });

    [Fact]
    public void WithNoFullResultToBeHad_TheClampedOneStaysAndTheRowSaysWhy() => HeadlessAvalonia.Run(async () =>
    {
        // No session behind the row is the same standing as a transcript that has been cleaned up or came from
        // another machine: the full result cannot be had. The row must say that, not fail and not go blank.
        var row = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: Bash");
        row.SetResult(Huge, isError: false);
        var clamped = row.ResultText;

        await row.LoadFullResultCommand.ExecuteAsync(null);

        Assert.Equal(clamped, row.ResultText);
        Assert.True(row.IsTruncated);
        Assert.Contains("no longer available", row.FullResultNotice!);
    });

    private static TranscriptEntryViewModel _Row(SessionViewModel session, string toolUseId) =>
        session.Transcript.Single(r => r.ToolUseId == toolUseId && r.Kind == TranscriptEntryKind.ToolUse);

    // Drives one place from the table through the session's own event entry point, and hands back the string
    // that place ends up holding.
    private static string _Drive(SessionViewModel session, string place)
    {
        switch (place)
        {
            case "a result coupled to its call":
                session.Apply(Call("t1", """{"command":"cat big.log"}"""));
                session.Apply(Result("t1", Huge));
                return _Row(session, "t1").ResultText!;

            case "a standalone result row":
                session.Apply(Result("t2", Huge));
                return session.Transcript.Single(r => r.Kind == TranscriptEntryKind.ToolResult && r.ToolUseId == "t2").Text;

            case "a call's input json":
                session.Apply(Call("t3", Huge, tool: "Write"));
                return _Row(session, "t3").InputJson!;

            case "a call's own row text":
                session.Apply(Call("t4", Huge, tool: "Write"));
                return _Row(session, "t4").Text;

            case "a permission request's input":
                session.Apply(new PermissionRequested
                {
                    SessionId = "s1", ToolUseId = "t5", ToolName = "Write", InputJson = Huge,
                });
                return _Row(session, "t5").InputJson!;

            case "streamed assistant text":
                // Delta by delta, the way it really arrives: the cap has to hold on the running total.
                foreach (var chunk in Enumerable.Range(0, 40).Select(_ => new string('y', ToolOutputBudget.MaxChars / 8)))
                {
                    session.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = chunk });
                }

                return session.Transcript.Last(r => r.Kind == TranscriptEntryKind.AssistantText).Text;

            default:
                // A sub-agent's own rows are full entries in their own right, held under the Task row that spawned them.
                session.Apply(Call("task1", """{"prompt":"go"}""", tool: "Task"));
                session.Apply(Call("t6", """{"command":"cat big.log"}""", parent: "task1"));
                session.Apply(Result("t6", Huge, parent: "task1"));
                var anchor = _Row(session, "task1");
                return anchor.SubAgentRows.Single(r => r.ToolUseId == "t6" && r.Kind == TranscriptEntryKind.ToolUse).ResultText!;
        }
    }
}
