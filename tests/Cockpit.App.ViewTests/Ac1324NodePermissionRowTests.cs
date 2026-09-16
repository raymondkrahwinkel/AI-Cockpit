using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1324: a question a session on a paired node is stopped on is drawn in the controller's own conversation — the
// same Allow/Deny row a local session shows, with the machine on it — and the click there answers it on the node.
// Measured on the full CockpitView in the Simple stand, through the relay the poll uses and a substituted client.
[Collection("avalonia")]
public class Ac1324NodePermissionRowTests
{
    private const string Node = "LAPTOP";

    private const string Pane = "0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Noon = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    // Criteria 1 and 2: after the poll the row names the machine and the tool, with Allow and Deny but no always-rule;
    // Allow goes over the line with the node's own ids and the row closes. Counter-proofs on the same screen: a
    // question the node already answered closes with that said and nothing else; a failed line leaves it open, with why.
    [Fact]
    public async Task ANodeQuestion_StandsInTheConversationWithTheMachine_AndTheClickAnswersItThere() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var window = Screenshotter.ShowScene("simple-view");
        try
        {
            var cockpit = (CockpitViewModel)window.DataContext!;
            cockpit.SimpleSelectedSession = null;
            var client = Substitute.For<INodeSessionsClient>();
            var relay = new NodePermissionRelay(client, () => cockpit.AssistantChat?.Session);
            var conversation = cockpit.AssistantChat!.Session!;

            relay.Reconcile(_Snapshot(("toolu_1", "Bash", "{\"command\":\"dotnet build\"}"), ("toolu_2", "Edit", "{\"file_path\":\"a.cs\"}"), ("toolu_3", "Bash", "{\"command\":\"git push\"}")));
            window.UpdateLayout();

            var header = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.IsEffectivelyVisible && t.Text == "Bash on LAPTOP  ·  dotnet build");
            var rowView = header.GetVisualAncestors().OfType<Controls.TranscriptRowView>().First();
            Assert.True(_Button(rowView, "Allow").IsEffectivelyVisible);
            Assert.True(_Button(rowView, "Deny").IsEffectivelyVisible);
            Assert.DoesNotContain(rowView.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content is "Always (exact)" or "Always (wildcard)");

            // Answered there — clicked twice, the way a double-click lands: one answer goes over the line.
            client.AnswerPermissionAsync(Node, Pane, "toolu_1", true, Arg.Any<CancellationToken>()).Returns(new NodePermissionAnswer(true));
            var allow = _Button(rowView, "Allow");
            await _ClickAsync(allow);
            await _ClickAsync(allow);
            window.UpdateLayout();
            await client.Received(1).AnswerPermissionAsync(Node, Pane, "toolu_1", true, Arg.Any<CancellationToken>());
            var row = conversation.Transcript.Single(r => r.ToolUseId == "toolu_1");
            Assert.False(row.IsPendingPermission);
            Assert.Equal("Allowed on LAPTOP", row.PermissionDecision);
            Assert.DoesNotContain(rowView.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content is "Allow");

            // Already answered on the node before the click landed.
            client.AnswerPermissionAsync(Node, Pane, "toolu_2", false, Arg.Any<CancellationToken>()).Returns(new NodePermissionAnswer(false));
            await _ClickAsync(_Button(_RowOf(window, "Edit on LAPTOP  ·  a.cs"), "Deny"));
            var gone = conversation.Transcript.Single(r => r.ToolUseId == "toolu_2");
            Assert.False(gone.IsPendingPermission);
            Assert.Equal("Already answered on LAPTOP", gone.PermissionDecision);

            // The line did not hold: still open, and the row says why.
            client.AnswerPermissionAsync(Node, Pane, "toolu_3", true, Arg.Any<CancellationToken>()).Returns(new NodePermissionAnswer(false, "LAPTOP did not answer within 10s."));
            await _ClickAsync(_Button(_RowOf(window, "Bash on LAPTOP  ·  git push"), "Allow"));
            var open = conversation.Transcript.Single(r => r.ToolUseId == "toolu_3");
            Assert.True(open.IsPendingPermission);
            Assert.Contains("did not answer", open.PermissionDecision);

            // The next poll: the same question is not drawn twice, and one answered on the node meanwhile closes.
            relay.Reconcile(_Snapshot(("toolu_3", "Bash", "{\"command\":\"git push\"}")));
            Assert.Equal(3, conversation.Transcript.Count(r => r.NodePermission is not null));
            Assert.True(open.IsPendingPermission);
            relay.Reconcile(_Snapshot());
            Assert.False(open.IsPendingPermission);
            Assert.Equal("Answered on LAPTOP", open.PermissionDecision);
        }
        finally
        {
            window.Close();
        }
    });

    private static NodeSessionsSnapshot _Snapshot(params (string ToolUseId, string Tool, string Input)[] questions) =>
        new(Node,
            [new NodeSessionRow(Pane, "AC-1", "Laptop Sonnet", "", "NeedsAttention", NeedsYou: questions.Length > 0,
                PendingPermissions: [.. questions.Select(q => new NodePendingPermission(q.ToolUseId, q.Tool, q.Input, Noon))])],
            [], []);

    private static Controls.TranscriptRowView _RowOf(Window window, string header) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == header)
            .GetVisualAncestors().OfType<Controls.TranscriptRowView>().First();

    private static Button _Button(Control within, string content) =>
        within.GetVisualDescendants().OfType<Button>().First(b => b.Content is string text && text == content);

    // The click as the button would carry it: the same command with the same parameter, awaited so the answer
    // over the line has landed before anything is asserted.
    private static Task _ClickAsync(Button button) =>
        ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)button.Command!).ExecuteAsync(button.CommandParameter);
}
