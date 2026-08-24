using Cockpit.App.Services;
using Cockpit.Infrastructure;
using Cockpit.Plugins.Abstractions.Projects;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Core.Tests.Projects;

// AC-894. Every test here drives `RunOnceAsync`/`SyncNowAsync` against a stubbed `ISharedProjectSource`: no Depot,
// no network, and no write ever attempted — this watcher only ever reads a checksum and reports whether it moved.
public class DepotSyncWatcherTests
{
    private readonly ISharedProjectSource _source = Substitute.For<ISharedProjectSource>();

    private DepotSyncWatcher _Watcher(string projectId = "proj-1", string sharedId = "depot:proj-1") =>
        new() { BoundProjects = () => [new DepotBoundProject(projectId, _source, sharedId)] };

    private void _Answers(params string?[] checksums)
    {
        var remaining = new Queue<string?>(checksums);
        _source.PrepareBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => remaining.Count > 0 && remaining.Dequeue() is { } checksum
                ? SharedProjectBindingResult.Success(new SharedProjectBinding("Name") { Checksum = checksum })
                : SharedProjectBindingResult.Failed("gone"));
    }

    // The first look at a project has nothing to compare its checksum against — it establishes the baseline rather
    // than reporting a change nobody could tell was new.
    [Fact]
    public async Task TheFirstCheckOnAProject_EstablishesTheBaselineWithoutReportingAChange()
    {
        _Answers("checksum-1");
        var reports = new List<(string ProjectId, bool Changed)>();
        using var watcher = _Watcher();
        watcher.OnChecked = (id, changed, _) => { reports.Add((id, changed)); return Task.CompletedTask; };

        await watcher.RunOnceAsync();

        Assert.Equal([("proj-1", false)], reports);
    }

    // Criterion 1/3: a checksum that moved between two ticks is exactly the signal the operator gets — the whole
    // point of the ticket.
    [Fact]
    public async Task AChecksumThatMovesBetweenTicks_IsReportedAsChanged()
    {
        _Answers("checksum-1", "checksum-2");
        var reports = new List<(string ProjectId, bool Changed)>();
        using var watcher = _Watcher();
        watcher.OnChecked = (id, changed, _) => { reports.Add((id, changed)); return Task.CompletedTask; };

        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();

        Assert.Equal([("proj-1", false), ("proj-1", true)], reports);
    }

    // A project that stays exactly where it was must not be reported as changed on every tick, or the badge would
    // never go quiet.
    [Fact]
    public async Task AChecksumThatStaysTheSame_IsNeverReportedAsChanged()
    {
        _Answers("checksum-1", "checksum-1");
        var reports = new List<bool>();
        using var watcher = _Watcher();
        watcher.OnChecked = (_, changed, _) => { reports.Add(changed); return Task.CompletedTask; };

        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();

        Assert.Equal([false, false], reports);
    }

    // Criterion 2: a source that fails (unreachable, not signed in) must not stop the rest of the sweep or crash the
    // tick — it is simply silent, the same contract `ISharedProjectSource.PrepareBindingAsync` already documents.
    [Fact]
    public async Task ASourceThatFails_ReportsNothingAndDoesNotThrow()
    {
        _source.PrepareBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SharedProjectBindingResult.Failed("unreachable"));
        var called = false;
        using var watcher = _Watcher();
        watcher.OnChecked = (_, _, _) => { called = true; return Task.CompletedTask; };

        await watcher.RunOnceAsync();

        Assert.False(called);
    }

    // Nothing wired means nothing to poll — the same "no orphan-everything" guard `WorktreeReconciler` keeps for an
    // absent live set.
    [Fact]
    public async Task WithNothingWiredToPollAgainst_ItDoesNothing()
    {
        using var watcher = new DepotSyncWatcher();

        await watcher.RunOnceAsync();

        await _source.DidNotReceiveWithAnyArgs().PrepareBindingAsync(default!, default);
    }

    // A sweep that outlasts the interval must not have a second one started on top of it, the same guard
    // `WorktreeReconciler`/`CiWatcher` already keep against two ticks racing the same state.
    [Fact]
    public async Task ATickOnTopOfARunningSweep_IsDropped()
    {
        var firstLook = new TaskCompletionSource<SharedProjectBindingResult>();
        var started = 0;
        _source.PrepareBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++started == 1 ? firstLook.Task : Task.FromResult(SharedProjectBindingResult.Failed("n/a")));
        using var watcher = _Watcher();

        var running = watcher.RunOnceAsync();
        await watcher.RunOnceAsync();
        firstLook.SetResult(SharedProjectBindingResult.Success(new SharedProjectBinding("Name") { Checksum = "c" }));
        await running;

        await _source.Received(1).PrepareBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The "Sync now" button's own seam: one project, checked immediately, independent of `RunOnceAsync` ever having
    // run at all.
    [Fact]
    public async Task SyncNowAsync_ChecksTheNamedProjectOutsideTheTimer()
    {
        _Answers("checksum-1");
        var reports = new List<string>();
        using var watcher = _Watcher();
        watcher.OnChecked = (id, _, _) => { reports.Add(id); return Task.CompletedTask; };

        await watcher.SyncNowAsync("proj-1");

        Assert.Equal(["proj-1"], reports);
    }

    // AC-1054: the bytes `PrepareBindingAsync` re-downloads every check — handed to `OnChecked` unchanged so the
    // cockpit can adopt a logo that arrived on the shared definition after this machine already bound it.
    [Fact]
    public async Task TheBindingsLogoBytes_AreHandedToOnChecked()
    {
        var expectedBytes = new byte[] { 1, 2, 3 };
        _source.PrepareBindingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SharedProjectBindingResult.Success(new SharedProjectBinding("Name") { Checksum = "c", LogoBytes = expectedBytes }));
        byte[]? reported = null;
        using var watcher = _Watcher();
        watcher.OnChecked = (_, _, logoBytes) => { reported = logoBytes; return Task.CompletedTask; };

        await watcher.RunOnceAsync();

        Assert.Equal(expectedBytes, reported);
    }

    // Asked of the container rather than of the class: an unregistered watcher resolves to null in `App.axaml.cs`,
    // which starts nothing — the timer dead with every test still green.
    [Fact]
    public async Task TheContainer_ResolvesTheWatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Core.DependencyInjection).Assembly,
            typeof(Infrastructure.DependencyInjection).Assembly,
            typeof(DepotSyncWatcher).Assembly);

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<DepotSyncWatcher>());
    }
}
