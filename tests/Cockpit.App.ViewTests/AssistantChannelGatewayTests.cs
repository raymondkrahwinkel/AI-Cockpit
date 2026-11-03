using System.Text.Json;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Tests.Shared;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The host half of a chat channel (AC-1023): who gets through to the assistant, what a channel is told about the
/// conversation, and which consent prompts it may answer. The fake channel here is the test itself — it sends and
/// records, and no Discord or Slack is anywhere near it, which is criterion 2.
/// </summary>
[Collection("avalonia")]
public class AssistantChannelGatewayTests
{
    private const string Allowed = "117";

    /// <summary>
    /// A send is awaited to completion, so a failure inside it is a refusal rather than a <c>Sent()</c> that was
    /// never true. Guards the unwrapping <c>InvokeAsync</c> overload the dispatched path relies on.
    /// </summary>
    [Fact]
    public Task ASendThatFails_IsRefusedWithItsReason_AndNeverReportedAsSent() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, host, _, _) = _Gateway();
        host.SendAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("the assistant is not running")));

        // From the UI thread, where the gateway runs the send inline…
        var inline = await gateway.SendAsync(Allowed, "how far is the build?");

        Assert.False(inline.Ok);
        Assert.False(inline.Ignored);
        Assert.Equal("the assistant is not running", inline.Error);

        // …and from off it, where it goes through the dispatcher and the outer task must not hide the failure.
        var dispatched = await Task.Run(() => gateway.SendAsync(Allowed, "how far is the build?"));

        Assert.False(dispatched.Ok);
        Assert.Equal("the assistant is not running", dispatched.Error);
    });

    /// <summary>
    /// Criterion 3, at the door rather than in the plugin: a stranger reaches nothing, and the plugin is told so. The
    /// identity check comes first, so their attachment is never even decoded, and they get silence, not a reason.
    /// </summary>
    [Fact]
    public Task AStrangersImage_IsNeverDecodedAndNeverExplained() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, host, _, _) = _Gateway();

        var result = await gateway.SendAsync("118", "let me in", ["not an image at all"u8.ToArray()]);

        Assert.False(result.Ok);
        Assert.True(result.Ignored);
        Assert.Null(result.Error);
        Assert.Null(result.ImagesRefused);
        await host.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>());
    });

    // ── questions (AC-1553) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A question reaches the channel with its options and a correlation id, and a choice comes back to whoever
    /// asked: the assistant as the operator's next message (AC-1553), or the plugin that sent it to its handler
    /// (AC-1544), by the id (a button) or as the text fallback ("2"). A stranger's choice is nothing, and so is a
    /// button for a question already answered in the app. Two plugin questions wait: only the newest gets the "2".
    /// </summary>
    [Theory]
    [InlineData("assistant", Allowed, "button", 1, 0)]
    [InlineData("assistant", Allowed, "text", 1, 0)]
    [InlineData("assistant", "118", "button", 0, 0)]
    [InlineData("assistant", Allowed, "button after the app", 0, 0)]
    [InlineData("plugin", Allowed, "button", 0, 1)]
    [InlineData("plugin", Allowed, "text", 0, 1)]
    public Task AQuestion_ReachesTheChannelWithItsOptions_AndAChoiceComesBackToWhoeverAsked(
        string asker, string sender, string how, int toTheAssistant, int toThePlugin) => HeadlessAvalonia.RunAsync(async () =>
    {
        var session = _Session();
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(TestSessions.Assistant(session));
        var rows = new List<AssistantChannelRow>();
        var registry = new OperatorMessageRegistry(NullLogger<OperatorMessageRegistry>.Instance);
        var channel = new _RecordingMessageChannel();
        using var gateway = _Open(host, Substitute.For<IConsentBroker>(), rows, messages: (registry, channel));
        var handled = new List<(string CorrelationId, string Chosen)>();
        var asked = new AssistantChannelQuestion
        {
            Question = "Which desk?",
            Options = [new AssistantChannelOption("Alpha"), new AssistantChannelOption("Beta"), new AssistantChannelOption("Gamma")],
        };

        AssistantChannelQuestion question;
        string correlationId;
        if (asker == "assistant")
        {
            session.AddQuestion("Which desk?", JsonSerializer.Serialize(new
            {
                questions = new[] { new { question = "Which desk?", options = new[] { new { label = "Alpha" }, new { label = "Beta" }, new { label = "Gamma" } } } },
            }), []);
            var row = rows.First(candidate => candidate.Kind == AssistantChannelRowKind.Question);
            question = Assert.Single(row.Questions);
            correlationId = row.CorrelationId ?? throw new InvalidOperationException("The question row carries no correlation id.");
        }
        else
        {
            foreach (var text in new[] { "the older one", "the newest one" })
            {
                await registry.SendAsync("channel-1", "dm", new OperatorMessage { Text = text, Question = asked }, answer =>
                {
                    handled.Add((answer.CorrelationId, string.Join(", ", answer.Chosen)));
                    return Task.CompletedTask;
                }, CancellationToken.None);
            }

            var sent = channel.Sent[^1];
            question = sent.Question ?? throw new InvalidOperationException("The message went out without its question.");
            correlationId = sent.CorrelationId ?? throw new InvalidOperationException("The message went out without a correlation id.");
        }

        Assert.Equal(["Alpha", "Beta", "Gamma"], question.Options.Select(option => option.Label));
        Assert.Contains("Reply with 1, 2 or 3", question.FallbackPrompt(), StringComparison.Ordinal);

        if (how == "button after the app")
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "\"Which desk?\" → Alpha"));
        }

        _ = how == "text" ? await gateway.SendAsync(sender, "2") : await gateway.AnswerAsync(sender, correlationId, ["Beta"]);

        await host.Received(toTheAssistant).SendAsync("\"Which desk?\" → Beta", Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>());
        Assert.Equal(Enumerable.Repeat((correlationId, "Beta"), toThePlugin), handled);
    });

    // A channel is a door onto the assistant's conversation only — another session's prompt is not its to see or to
    // answer. AC-1360 adds a workflow's prompt that no session owns ("Ask me first"), and no other plugin's.
    [Theory]
    [InlineData("pane-someone-else", "workflows", 0)]
    [InlineData(null, "docker", 0)]
    [InlineData(null, "workflows", 1)]
    public void OnlyTheAssistantsOwnOrASessionlessWorkflowPrompt_IsRelayedAndAnswerable(string? paneId, string? pluginId, int relayed) => HeadlessAvalonia.Run(() =>
    {
        var broker = Substitute.For<IConsentBroker>();
        var prompts = new List<AssistantChannelConsentPrompt>();
        using var gateway = _Open(_Session(), broker, rows: []);
        gateway.ConsentPromptOpened += (_, prompt) => prompts.Add(prompt);

        var opened = _Prompt(paneId, pluginId);
        broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, opened);

        Assert.Equal(relayed, prompts.Count);

        // And knowing the id from somewhere else is not a way in either.
        gateway.RespondToConsent(opened.Id, ConsentOutcome.Approved);

        broker.Received(relayed).Respond(opened.Id, ConsentOutcome.Approved, false);
    });

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    private static ConsentPrompt _Prompt(string? paneId, string? pluginId = null) => new(
        Guid.NewGuid(),
        new ConsentRequest("The assistant wants to run a command", "rm -rf /tmp/build", new ConsentSource(paneId, pluginId, "Assistant"), "bash", ConsentRisk.Dangerous),
        CanRemember: false);

    private static SessionViewModel _Session()
    {
        // The parameterless constructor is the previewer's and seeds sample rows; a real conversation starts empty.
        var session = new SessionViewModel();
        session.Transcript.Clear();
        return session;
    }

    private static AssistantChannelGateway _Open(SessionViewModel session, IConsentBroker broker, List<AssistantChannelRow> rows)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(TestSessions.Assistant(session));

        return _Open(host, broker, rows);
    }

    private static AssistantChannelGateway _Open(
        IAssistantSessionHost host,
        IConsentBroker broker,
        List<AssistantChannelRow> rows,
        ILogger<AssistantChannelGateway>? logger = null,
        (OperatorMessageRegistry Registry, IOperatorMessageChannel Channel)? messages = null)
    {
        var channel = new AssistantChannelContribution
        {
            Id = "channel-1",
            Name = "Test channel",
            Access = AssistantChannelAccess.ForSingleUser(Allowed).Access!,
            Messages = messages?.Channel,
        };
        var gateway = new AssistantChannelGateway(
            channel, host, broker, logger ?? NullLogger<AssistantChannelGateway>.Instance, messages?.Registry);
        gateway.RowChanged += (_, row) => rows.Add(row);

        return gateway;
    }

    private static (AssistantChannelGateway Gateway, IAssistantSessionHost Host, SessionViewModel Session, List<AssistantChannelRow> Rows) _Gateway()
    {
        var session = _Session();
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(TestSessions.Assistant(session));
        var rows = new List<AssistantChannelRow>();

        return (_Open(host, Substitute.For<IConsentBroker>(), rows), host, session, rows);
    }

    // A channel without buttons, as far as the host can tell: it takes every message to its one target and keeps it.
    private sealed class _RecordingMessageChannel : IOperatorMessageChannel
    {
        public List<OperatorMessage> Sent { get; } = [];

        public IReadOnlyList<OperatorMessageTarget> Targets { get; } = [new OperatorMessageTarget("dm", "Direct message")];

        public Task<OperatorMessageResult> SendAsync(string targetId, OperatorMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.FromResult(OperatorMessageResult.Sent());
        }
    }
}
