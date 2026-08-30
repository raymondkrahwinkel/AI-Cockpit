using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Sessions;

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
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);
        services.AddSessionPanes();

        return services;
    }

    [Fact]
    public async Task Container_ResolvesTheTranscriptLog()
    {
        await using var provider = _ProductionServices().BuildServiceProvider();

        Assert.IsType<SessionTranscriptLog>(provider.GetService<ISessionTranscriptStore>());
    }

    // THE ACCEPTANCE TEST. Red without the constructor argument on `SessionViewModel` (the pane resolves fine and
    // writes nothing), green with it. The store is the real one, pointed at a temp folder rather than the
    // operator's state root — the registration is replaced, not the wiring being tested.
    [Fact]
    public async Task APaneTakenFromTheContainer_RecordsItsRowsToDisk()
    {
        var services = _ProductionServices();
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);
        services.AddSingleton<ISessionTranscriptStore>(store);

        await using var provider = services.BuildServiceProvider();
        var pane = provider.GetRequiredService<Func<SessionViewModel>>()();

        pane.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "what is the status of AC-1090"));
        await store.FlushAsync(CancellationToken.None);

        var recorded = await store.LoadAsync(pane.PaneId);
        Assert.Equal("what is the status of AC-1090", Assert.Single(recorded).Text);
    }

    // The other half of the same wiring: what was recorded comes back onto the pane, rebuilt rather than merely
    // counted — a chip that replays without its tool name and result is a row the operator cannot read.
    [Fact]
    public async Task APaneTakenFromTheContainer_ReplaysWhatItRecorded()
    {
        var services = _ProductionServices();
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);
        services.AddSingleton<ISessionTranscriptStore>(store);

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<Func<SessionViewModel>>();

        var first = factory();
        var toolRow = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "")
        {
            ToolName = "Bash",
            InputJson = """{"command":"ls"}""",
            ToolUseId = "tool-1",
        };
        first.Transcript.Add(toolRow);
        toolRow.SetResult("file.txt", isError: false);
        await store.FlushAsync(CancellationToken.None);

        // A different pane object with the same id — the restart this layer exists for.
        var restarted = factory();
        restarted.AdoptPaneId(first.PaneId);
        await restarted.ReplayRecordedTranscriptAsync();

        var replayed = Assert.Single(restarted.Transcript);
        Assert.Equal(TranscriptEntryKind.ToolUse, replayed.Kind);
        Assert.Equal("Bash", replayed.ToolName);
        Assert.Equal("file.txt", replayed.ResultText);
        Assert.Equal(toolRow.Id, replayed.Id);
    }

    // A replayed row must not be recorded straight back: it is already in the log, and appending it again on every
    // restart is how an append-only log quietly turns into the amplification it replaced.
    [Fact]
    public async Task ReplayedRows_AreNotRecordedAgain()
    {
        var services = _ProductionServices();
        var store = new SessionTranscriptLog(_tempDir, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);
        services.AddSingleton<ISessionTranscriptStore>(store);

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<Func<SessionViewModel>>();

        var first = factory();
        first.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "one"));
        first.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "two"));
        await store.FlushAsync(CancellationToken.None);
        var afterRecording = new FileInfo(store.LogPath(first.PaneId)).Length;

        var restarted = factory();
        restarted.AdoptPaneId(first.PaneId);
        await restarted.ReplayRecordedTranscriptAsync();
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal(afterRecording, new FileInfo(store.LogPath(first.PaneId)).Length);
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
