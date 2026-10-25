using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-1717: the session timeline. (a) L2 data loss: a restart closes what the last run left open, at its stop time.
// (b) L2 formats: the jsonl line, and a line of an older version (an unknown field) that stays readable.
// (c) L2 secrets: an operator line per sent message and per answer, and never the message or answer itself.
public sealed class SessionTimelineRecorderTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(Noon);

    private string _Path => Path.Combine(_directory, "session-timeline.jsonl");

    public SessionTimelineRecorderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ARestart_ClosesTheIntervalABusySessionLeftOpen_AtTheStopTime_WithTheTicketItsStatuslineNamed()
    {
        var (_, registry) = _Session("builder", "AC-9 timeline", SessionStatus.Busy);
        var recorder = new SessionTimelineRecorder(_Path, _clock);
        recorder.Start(registry);
        _clock.Now = Noon.AddMinutes(5);
        recorder.RecordStatusline("builder", "AC-1 reading");
        _clock.Now = Noon.AddMinutes(10);
        recorder.RecordStatusline("builder", "AC-2 writing");
        _clock.Now = Noon.AddMinutes(30);
        recorder.Dispose();

        _clock.Now = Noon.AddHours(2);
        using var reopened = new SessionTimelineRecorder(_Path, _clock);
        reopened.Start(_Registry());
        await reopened.FlushAsync();

        var lines = await _ReadAsync();
        Assert.Equal(
            [(SessionTimelineKind.Status, "AC-9"), (SessionTimelineKind.Statusline, "AC-1"), (SessionTimelineKind.Statusline, "AC-2")],
            lines.Take(3).Select(line => (line.Kind, line.Ticket)));
        var closed = lines[^1];
        Assert.Equal((SessionTimelineKind.Closed, "builder", "AC-2"), (closed.Kind, closed.PaneId, closed.Ticket));
        Assert.Equal(Noon.AddMinutes(30), closed.At);
        Assert.Equal("the-project", closed.ProjectId);
    }

    [Fact]
    public async Task ALineOfAnOlderVersion_WithAFieldThisOneDoesNotKnow_StaysReadable_AndLinesAreWrittenByName()
    {
        await File.WriteAllTextAsync(_Path,
            """{"Kind":"Status","At":"2026-10-25T12:00:00+00:00","PaneId":"old","Ticket":"AC-3","Status":"NeedsAttention","Mood":"later field"}""" + "\n"
            + """{"Kind":"Stopped","At":"2026-10-25T12:20:00+00:00"}""" + "\n");

        using var recorder = new SessionTimelineRecorder(_Path, _clock);
        recorder.Start(_Registry());
        await recorder.FlushAsync();

        var written = (await File.ReadAllLinesAsync(_Path))[^1];
        Assert.Contains("\"Kind\":\"Closed\"", written);
        Assert.Contains("\"PaneId\":\"old\"", written);
        Assert.Contains("\"Ticket\":\"AC-3\"", written);
        Assert.Contains("\"At\":\"2026-10-25T12:20:00+00:00\"", written);
    }

    [Fact]
    public async Task TwoTurnsAndAnAnsweredQuestion_GiveTheirStatusesAndOneOperatorLineEach_ButNeverWhatWasSentOrAnswered()
    {
        const string Message = "the unmistakable sentence nobody else writes 7f3a";
        const string Answer = "an answer that must stay out 9c1e";
        var (host, runtime) = _Host();
        var (handle, registry) = _Session("builder", "AC-4", SessionStatus.Idle);
        host.LiveStateChanged += state => handle.LiveStateChanged += Raise.Event<Action<SessionLiveState>>(state);
        host.OperatorActed += action => handle.OperatorActed += Raise.Event<Action<SessionOperatorAction>>(action);
        using var recorder = new SessionTimelineRecorder(_Path, _clock);
        recorder.Start(registry);

        for (var turn = 0; turn < 2; turn++)
        {
            _clock.Now = _clock.Now.AddMinutes(1);
            await host.SubmitAsync(new QueuedPrompt(Message, []));
            _clock.Now = _clock.Now.AddMinutes(1);
            host.Pump(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = Message, IsError = false });
        }

        handle.LiveStateChanged += Raise.Event<Action<SessionLiveState>>(SessionLiveState.None with { Status = SessionStatus.NeedsAttention });
        await host.RespondToPermissionAsync("tool-1", allow: true, answersJson: $$"""{"q":"{{Answer}}"}""");
        handle.LiveStateChanged += Raise.Event<Action<SessionLiveState>>(SessionLiveState.None with { Status = SessionStatus.Busy });
        await recorder.FlushAsync();

        var lines = await _ReadAsync();
        Assert.Equal(
            ["Status:Idle", "Operator:MessageSent", "Status:Busy", "Status:Done", "Operator:MessageSent", "Status:Busy", "Status:Done",
                "Status:NeedsAttention", "Operator:QuestionAnswered", "Status:Busy"],
            lines.Select(line => $"{line.Kind}:{(object?)line.Status ?? line.Action}"));
        Assert.True(lines.Zip(lines.Skip(1)).All(pair => pair.First.At <= pair.Second.At));
        var text = await File.ReadAllTextAsync(_Path);
        Assert.DoesNotContain("unmistakable sentence", text);
        Assert.DoesNotContain("must stay out", text);
        await runtime.Received(2).SendUserMessageAsync(Arg.Is<string>(sent => sent.Contains(Message)), Arg.Any<IReadOnlyList<ImageAttachment>?>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<IReadOnlyList<SessionTimelineEntry>> _ReadAsync() =>
        [.. (await new SessionTimelineLog(_Path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).ReadRecentAsync(int.MaxValue)).Reverse()];

    private static (ISessionHandle Handle, ISessionRegistry Registry) _Session(string paneId, string title, SessionStatus status)
    {
        var handle = Substitute.For<ISessionHandle>();
        handle.PaneId.Returns(paneId);
        handle.Title.Returns(title);
        handle.ProjectId.Returns("the-project");
        handle.SessionStatus.Returns(status);
        return (handle, _Registry(handle));
    }

    private static ISessionRegistry _Registry(params ISessionHandle[] handles)
    {
        var registry = Substitute.For<ISessionRegistry>();
        registry.All.Returns(handles);
        foreach (var handle in handles)
        {
            registry.Find(handle.PaneId).Returns(handle);
        }

        return registry;
    }

    private (SessionHost Host, ISessionRuntime Runtime) _Host()
    {
        var runtime = Substitute.For<ISessionRuntime>();
        runtime.IsRunning.Returns(true);
        var manager = Substitute.For<ISessionManager>();
        manager.Create(Arg.Any<SessionProfile?>()).Returns(runtime);
        var host = new SessionHost(() => "builder", manager, _clock);
        host.Attach(new SessionProfile("work", new ClaudeConfig("/fake/.claude")));
        return (host, runtime);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
