using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Sessions;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// AC-1090's layer reaches a pane through an optional constructor argument, which is exactly the shape that fails
/// silently: a container that does not fill it leaves every pane recording nothing — no exception, no log, and
/// every other test in this repository still green, because they all hand the store in by hand. So this builds the
/// real container the way <c>Program.cs</c> does, takes a pane out of it the way <c>CockpitViewModel</c> does, and
/// asks the file system whether a row actually landed. Nothing here asserts on a substitute.
/// </summary>
public class Ac1090_TranscriptRecordingWiringTests : IDisposable
{
    private readonly string _tempDir;

    public Ac1090_TranscriptRecordingWiringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private static ServiceCollection _ProductionServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddDesktopInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);
        services.AddSessionPanes();

        return services;
    }

    // THE ACCEPTANCE TEST. Red without the constructor argument on `SessionViewModel` (the pane resolves fine and
    // writes nothing), green with it. The store is the real one, pointed at a temp folder rather than the
    // operator's state root — the registration is replaced, not the wiring being tested.
    [Fact]
    public async Task APaneTakenFromTheContainer_RecordsItsRowsToDisk_AndDoesNotRecordReplayedRowsAgain()
    {
        var services = _ProductionServices();
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);
        services.AddSingleton<ISessionTranscriptStore>(store);

        await using var provider = services.BuildServiceProvider();
        var pane = provider.GetRequiredService<Func<SessionViewModel>>()();

        pane.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "what is the status of AC-1090"));
        await store.FlushAsync(CancellationToken.None);

        var recorded = (await store.TryLoadAsync(pane.PaneId))!;
        Assert.Equal("what is the status of AC-1090", Assert.Single(recorded).Text);

        // A replayed row must not be recorded straight back: appending it again on every restart is how an
        // append-only log quietly turns into the amplification it replaced.
        var afterRecording = new FileInfo(store.LogPath(pane.PaneId)).Length;
        var restarted = provider.GetRequiredService<Func<SessionViewModel>>()();
        restarted.AdoptPaneId(pane.PaneId);
        await restarted.ReplayRecordedTranscriptAsync();
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal(afterRecording, new FileInfo(store.LogPath(pane.PaneId)).Length);
        await _EphemeralSpeechDoesNotBecomeRecordedRowsAsync(store);
    }

    private static async Task _EphemeralSpeechDoesNotBecomeRecordedRowsAsync(SessionTranscriptLog store)
    {
        const string sentinel = "Private sentinel spoken only.";
        const string answer = "Het volledige antwoord blijft hier staan.";
        var profile = new SessionProfile("private", new OllamaConfig("http://unused.invalid", "fake"));
        var drivers = Substitute.For<ISessionDriverFactory>();
        drivers.GenerateEphemeralSpeechAsync(profile, answer, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(sentinel));
        var control = Substitute.For<ISessionControl>();
        var controls = Substitute.For<ISessionControlFactory>();
        controls.Create(Arg.Any<Func<string>>()).Returns(control);
        var playback = Substitute.For<IVoicePlaybackQueue>();
        var spoken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.When(queue => queue.CompleteStream(VoicePlaybackSource.Assistant, AssistantIdentity.PaneId)).Do(_ => spoken.TrySetResult());
        await using var pane = new SessionViewModel(controls, voicePlaybackQueue: playback, speechDrivers: drivers);
        pane.AdoptPaneId(AssistantIdentity.PaneId);
        pane.ReadResponsesAloud = true;
        pane.AssistantSpeechMode = SpokenReplyMode.Short;
        pane.AssistantSpeechMethod = SpokenReplyMethod.Profile;
        pane.SpeechProfile = profile;
        control.TurnStarting += Raise.Event<Action<QueuedPrompt>>(new QueuedPrompt("status", []));
        var row = TranscriptSnapshot.Capture(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, answer));
        control.RowUpserted += Raise.Event<Action<TranscriptRowUpsert>>(new TranscriptRowUpsert(1, 1, row));
        control.Folded += Raise.Event<Action<TranscriptFold>>(new TranscriptFold(row, false) { IsReply = true });
        control.TurnEnded += Raise.Event<Action<SessionTurnEnd>>(new SessionTurnEnd(false, false, false, null, "success", null));
        await spoken.Task;
        playback.Received().Enqueue(Arg.Is<IReadOnlyList<SpeechSegment>>(segments => string.Join(" ", segments.SelectMany(segment => segment.Sentences)) == sentinel),
            Arg.Any<int>(), VoicePlaybackSource.Assistant, pane.PaneId);
        var snapshots = pane.Transcript.Select(TranscriptSnapshot.Capture).ToArray();
        Assert.DoesNotContain(snapshots, snapshot => System.Text.Json.JsonSerializer.Serialize(snapshot).Contains(sentinel, StringComparison.Ordinal));
        Assert.Equal(answer, pane.Transcript.Single(entry => entry.Kind == TranscriptEntryKind.AssistantText).Text);
        await store.AppendAsync("ephemeral-proof", snapshots.Single(snapshot => snapshot.Kind == nameof(TranscriptEntryKind.AssistantText)));
        await store.FlushAsync(CancellationToken.None);
        Assert.Equal(answer, Assert.Single((await store.TryLoadAsync("ephemeral-proof")) ?? []).Text);
        drivers.DidNotReceive().Create(Arg.Any<SessionProfile?>());

        using var cancelled = new CancellationTokenSource();
        var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        drivers.GenerateEphemeralSpeechAsync(profile, answer, null, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return late.Task;
        });
        var pending = SpokenReplyGeneration.GenerateAsync(drivers, profile, answer, null, SpokenReplyMode.Short, SpokenReplyMethod.Assistant, cancelled.Token);
        await entered.Task;
        cancelled.Cancel();
        late.SetResult(sentinel);
        Assert.Empty((await pending).Sentences);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
