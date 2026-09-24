using SkiaSharp;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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

    // ── inbound (§4) ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AMessageFromTheAllowedAccount_TakesTheSameRouteAsTypedText() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, host, _, _) = _Gateway();

        var result = await gateway.SendAsync(Allowed, "how far is the build?");

        Assert.True(result.Ok);
        Assert.False(result.Ignored);
        await host.Received().SendAsync("how far is the build?", Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>());
    });

    /// <summary>
    /// Criterion 3, at the door rather than in the plugin: a stranger gets silence, and the plugin is told so rather than being trusted to check first.
    /// </summary>
    [Fact]
    public Task AMessageFromAnyOtherAccount_ReachesNothingAndIsAnsweredWithSilence() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, host, _, _) = _Gateway();

        var result = await gateway.SendAsync("118", "let me in");

        Assert.False(result.Ok);
        Assert.True(result.Ignored);
        Assert.Null(result.Error);
        await host.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>());
    });

    /// <summary>
    /// AC-1074: silence towards the sender, never towards the operator. This drop is the whole reason a Slack
    /// message could vanish for two hours with nothing anywhere to show for it.
    /// </summary>
    [Fact]
    public Task AnIgnoredSender_LeavesTheReasonAndTheSenderIdInTheLog() => HeadlessAvalonia.RunAsync(async () =>
    {
        var logger = new _RecordingLogger();
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(_Session());
        var gateway = _Open(host, Substitute.For<IConsentBroker>(), [], logger);

        await gateway.SendAsync("118", "let me in");

        var (level, message) = Assert.Single(logger.Entries);

        // Below Information is the same as not logging at all: FileLoggerProvider drops it, which is exactly how
        // the original Debug line managed to exist and still leave the operator with nothing.
        Assert.True(level >= LogLevel.Information, $"logged at {level}, which never reaches cockpit.log");
        Assert.Contains("118", message, StringComparison.Ordinal);
        Assert.Contains("access list", message, StringComparison.Ordinal);
    });

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

    // ── images (AC-1049) ───────────────────────────────────────────────────────────────────────────────────────

    private static byte[] _Png(int width = 32, int height = 24)
    {
        using var bitmap = new SKBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    [Fact]
    public Task AnImageThatIsOne_ReachesTheAssistantOnTheSameMessageAsTheText() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, host, _, _) = _Gateway();

        var result = await gateway.SendAsync(Allowed, "look at this", [_Png()]);

        Assert.True(result.Ok);
        Assert.Null(result.ImagesRefused);
        await host.Received().SendAsync(
            "look at this",
            Arg.Is<IReadOnlyList<byte[]>>(images => images.Count == 1),
            Arg.Any<CancellationToken>());
    });

    /// <summary>
    /// Criterion 5: the plugin said it was an image, the codec disagreed. The attachment is dropped on its own
    /// and the sentence it came with still reaches the assistant.
    /// </summary>
    [Fact]
    public Task SomethingThatIsNotAnImage_IsRefusedWhileItsTextGoesThroughAnyway() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, host, _, _) = _Gateway();

        var result = await gateway.SendAsync(Allowed, "what do you make of this", ["not an image at all"u8.ToArray()]);

        Assert.True(result.Ok);
        Assert.Contains("not an image", result.ImagesRefused);
        await host.Received().SendAsync(
            "what do you make of this",
            Arg.Is<IReadOnlyList<byte[]>>(images => images.Count == 0),
            Arg.Any<CancellationToken>());
    });

    [Fact]
    public Task MoreImagesThanOneMessageMayCarry_AreCutBackAndTheSenderIsTold() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, host, _, _) = _Gateway();
        var tooMany = Enumerable.Range(0, AssistantChannelImageLimits.MaxPerMessage + 1).Select(_ => _Png()).ToList();

        var result = await gateway.SendAsync(Allowed, "lots", tooMany);

        Assert.True(result.Ok);
        Assert.Contains($"first {AssistantChannelImageLimits.MaxPerMessage}", result.ImagesRefused);
        await host.Received().SendAsync(
            "lots",
            Arg.Is<IReadOnlyList<byte[]>>(images => images.Count == AssistantChannelImageLimits.MaxPerMessage),
            Arg.Any<CancellationToken>());
    });

    /// <summary>
    /// The identity check comes first, so a stranger's attachment is never even decoded — and they are still
    /// answered with silence rather than with a reason.
    /// </summary>
    [Fact]
    public Task AStrangersImage_IsNeverDecodedAndNeverExplained() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, host, _, _) = _Gateway();

        var result = await gateway.SendAsync("118", "let me in", ["not an image at all"u8.ToArray()]);

        Assert.True(result.Ignored);
        Assert.Null(result.ImagesRefused);
        await host.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>());
    });

    // ── outbound (§4) ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARowArriving_IsRelayedOnce_AndTheSameRowGrowing_ComesBackAsAnUpdateOfIt() => HeadlessAvalonia.Run(() =>
    {
        var (gateway, _, session, rows) = _Gateway();

        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "The build ");
        session.Transcript.Add(entry);
        entry.AppendText("is green.");

        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsUpdate);
        Assert.Equal("The build ", rows[0].Text);
        Assert.Equal(AssistantChannelRowKind.AssistantText, rows[0].Kind);

        Assert.True(rows[1].IsUpdate);
        Assert.Equal("The build is green.", rows[1].Text);
        Assert.Equal(rows[0].Id, rows[1].Id);
    });

    [Fact]
    public void ToolRows_CarryTheirToolAndItsResult() => HeadlessAvalonia.Run(() =>
    {
        var (gateway, _, session, rows) = _Gateway();

        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "dotnet build") { ToolName = "Bash" };
        session.Transcript.Add(entry);
        entry.SetResult("Build succeeded.", isError: false);

        Assert.Equal(AssistantChannelRowKind.ToolUse, rows[0].Kind);
        Assert.Equal("Bash", rows[0].ToolName);
        Assert.Equal("Build succeeded.", rows[^1].ResultText);
    });

    /// <summary>
    /// A channel that connects mid-conversation says what happens next, never replays what was already said.
    /// </summary>
    [Fact]
    public void RowsAlreadyInTheTranscriptWhenAChannelOpens_AreNotReplayed() => HeadlessAvalonia.Run(() =>
    {
        var session = _Session();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "said before the channel existed"));

        var rows = new List<AssistantChannelRow>();
        using var gateway = _Open(session, Substitute.For<IConsentBroker>(), rows);

        Assert.Empty(rows);

        // But it is watched from here on: the old row growing is an update the channel does hear about.
        session.Transcript[0].AppendText(" …and continued");
        Assert.Single(rows);
        Assert.True(rows[0].IsUpdate);
    });

    // AC-1379: the rows now come from the host's upsert stream rather than the pane's own view models, so the channel
    // must see the rows the pane shows, in its order, a reply split at a blank line included. Red when one is missing or doubled.
    [Fact]
    public void TheChannel_SeesTheRowsThePaneShows_InItsOrder_ASplitReplyIncluded() => HeadlessAvalonia.Run(() =>
    {
        var (gateway, _, session, rows) = _Gateway();

        _Apply(
            session,
            new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "First paragraph.\n\n" },
            new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Second paragraph, " },
            new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "still growing." },
            new AssistantTextCompleted { SessionId = "S1", Text = "First paragraph.\n\nSecond paragraph, still growing." });
        ((IAssistantSession)session).AddDivider("Context was full");

        var relayed = rows.GroupBy(row => row.Id).Select(group => (group.First().Kind.ToString(), group.Last().Text));
        Assert.Equal(session.Transcript.Select(entry => (entry.Kind.ToString(), entry.Text)), relayed);
        Assert.Equal(2, session.Transcript.Count(entry => entry.Kind == TranscriptEntryKind.AssistantText));
        Assert.Equal(rows.Select(row => row.Id).Distinct().Count(), rows.Count(row => !row.IsUpdate));
    });

    [Fact]
    public void ADisposedChannel_HearsNothingMore() => HeadlessAvalonia.Run(() =>
    {
        var (gateway, _, session, rows) = _Gateway();

        gateway.Dispose();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "after closing"));

        Assert.Empty(rows);
    });

    // ── consent (§5) ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheAssistantsOwnPrompt_IsRelayed_AndAnsweringItReachesTheBroker() => HeadlessAvalonia.Run(() =>
    {
        var broker = Substitute.For<IConsentBroker>();
        var prompts = new List<AssistantChannelConsentPrompt>();
        using var gateway = _Open(_Session(), broker, rows: []);
        gateway.ConsentPromptOpened += (_, prompt) => prompts.Add(prompt);

        var opened = _Prompt(AssistantIdentity.PaneId);
        broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, opened);

        Assert.Single(prompts);
        Assert.Equal(opened.Id, prompts[0].Id);
        Assert.Equal("rm -rf /tmp/build", prompts[0].Request.Action);

        gateway.RespondToConsent(opened.Id, ConsentOutcome.Approved);

        broker.Received().Respond(opened.Id, ConsentOutcome.Approved, false);
    });

    // A channel is a door onto the assistant's conversation only — another session's prompt is not its to see or to
    // answer. AC-1360 adds a workflow's prompt that no session owns ("Ask me first"), and no other plugin's.
    [Theory]
    [InlineData("pane-someone-else", null, 0)]
    [InlineData("pane-someone-else", "workflows", 0)]
    [InlineData(null, null, 0)]
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

    [Fact]
    public void AResolvedPrompt_TakesTheChannelsSurfaceDownToo() => HeadlessAvalonia.Run(() =>
    {
        var broker = Substitute.For<IConsentBroker>();
        var closed = new List<Guid>();
        using var gateway = _Open(_Session(), broker, rows: []);
        gateway.ConsentPromptClosed += (_, id) => closed.Add(id);

        var opened = _Prompt(AssistantIdentity.PaneId);
        broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, opened);
        broker.PromptClosed += Raise.Event<EventHandler<Guid>>(broker, opened.Id);

        Assert.Equal([opened.Id], closed);

        // Answered by the app's own card in the meantime: the id is spent, and a late click from the channel is a no-op.
        gateway.RespondToConsent(opened.Id, ConsentOutcome.Approved);
        broker.DidNotReceive().Respond(Arg.Any<Guid>(), Arg.Any<ConsentOutcome>(), Arg.Any<bool>());
    });

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    private static ConsentPrompt _Prompt(string? paneId, string? pluginId = null) => new(
        Guid.NewGuid(),
        new ConsentRequest("The assistant wants to run a command", "rm -rf /tmp/build", new ConsentSource(paneId, pluginId, "Assistant"), "bash", ConsentRisk.Dangerous),
        CanRemember: false);

    private static void _Apply(SessionViewModel session, params SessionEvent[] events) => Array.ForEach(events, session.Apply);

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
        host.Session.Returns(session);

        return _Open(host, broker, rows);
    }

    private static AssistantChannelGateway _Open(
        IAssistantSessionHost host,
        IConsentBroker broker,
        List<AssistantChannelRow> rows,
        ILogger<AssistantChannelGateway>? logger = null)
    {
        var channel = new AssistantChannelContribution
        {
            Id = "channel-1",
            Name = "Test channel",
            Access = AssistantChannelAccess.ForSingleUser(Allowed).Access!,
        };
        var gateway = new AssistantChannelGateway(channel, host, broker, logger ?? NullLogger<AssistantChannelGateway>.Instance);
        gateway.RowChanged += (_, row) => rows.Add(row);

        return gateway;
    }

    private static (AssistantChannelGateway Gateway, IAssistantSessionHost Host, SessionViewModel Session, List<AssistantChannelRow> Rows) _Gateway()
    {
        var session = _Session();
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        var rows = new List<AssistantChannelRow>();

        return (_Open(host, Substitute.For<IConsentBroker>(), rows), host, session, rows);
    }

    private sealed class _RecordingLogger : ILogger<AssistantChannelGateway>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
