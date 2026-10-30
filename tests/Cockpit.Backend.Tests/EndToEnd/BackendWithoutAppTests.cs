using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Configuration;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Ci;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Hotkeys;
using SoundFlow.Abstracts;
using Cockpit.Core.Diagnostics;
using NSubstitute;

namespace Cockpit.Backend.Tests.EndToEnd;

[CollectionDefinition(BackendWithoutAppTests.Alone, DisableParallelization = true)]
public sealed class BackendWithoutAppCollection;

// The state root is process-wide, so these backend startup checks run alone.
[Collection(Alone)]
public sealed class BackendWithoutAppTests : IDisposable
{
    public const string Alone = "Backend without App: the process-wide state root";

    private readonly string? _previousStateRoot = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), $"backend-without-app-{Guid.NewGuid():N}");

    public BackendWithoutAppTests()
    {
        Directory.CreateDirectory(_stateRoot);
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _stateRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, _previousStateRoot);

        try
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
        catch (IOException)
        {
            // The clone reconcile Start leaves running may still hold a file there; a temp folder the OS clears is fine.
        }
    }

    // A planner started before the startup reconcile is refused: its crash net assumes the sweep ran. AC-1889: the
    // background start hands back at once while a planner takes 3 s; a second call does not start again.
    [Fact]
    public async Task ThePlanners_StartOnlyAfterTheStartupReconcile_AndNeverHoldTheCaller()
    {
        var maintenance = Substitute.For<IDockerStorageMaintenance>();
        var backend = CockpitBackend.Build(NullLoggerFactory.Instance, services => services.AddSingleton(maintenance));
        await using var services = backend.Services;

        var early = Record.Exception(backend.StartPlanners);
        var watchingBeforeStart = services.GetRequiredService<CiWatcher>().Watching;
        backend.Start();
        var starts = 0;
        backend.BeforePlanners = () =>
        {
            starts++;
            Thread.Sleep(TimeSpan.FromSeconds(3));
        };
        var clock = Stopwatch.StartNew();
        var started = backend.StartPlannersInBackgroundAsync();
        var handedBackIn = clock.Elapsed;
        await Task.WhenAll(started, backend.StartPlannersInBackgroundAsync());

        Assert.IsType<InvalidOperationException>(early);
        Assert.Null(watchingBeforeStart);
        Assert.True(handedBackIn < TimeSpan.FromSeconds(1), $"The caller was held for {handedBackIn}.");
        Assert.Equal(1, starts);
        Assert.NotNull(services.GetRequiredService<CiWatcher>().Watching);
        maintenance.DidNotReceive().Start();
    }

    [Fact]
    public async Task ABackendWithoutTheDesktop_UsesNoHotkeysAndResolvesNoUiOnlyServices()
    {
        var backend = CockpitBackend.Build(NullLoggerFactory.Instance);
        await using var services = backend.Services;

        Assert.IsType<NoOpGlobalHotkeyService>(services.GetRequiredService<IGlobalHotkeyService>());
        Assert.Null(services.GetService<AudioEngine>());
        Assert.Null(services.GetService<ISpeechToTextService>());
        Assert.Null(services.GetService<IScreenshotCapture>());
    }
}
