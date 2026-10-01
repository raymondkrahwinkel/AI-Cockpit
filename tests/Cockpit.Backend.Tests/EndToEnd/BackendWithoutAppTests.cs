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

namespace Cockpit.Backend.Tests.EndToEnd;

[CollectionDefinition(BackendWithoutAppTests.Alone, DisableParallelization = true)]
public sealed class BackendWithoutAppCollection;

// AC-1381: the backend built by `CockpitBackend` on a fresh state root, with no App, no plugins and no stub for any
// seam. AC-1425: what the journeys already start went; what is left is the order the worktree planner depends on
// (data loss). Alone, because the state root is the process environment's.
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

    // A planner started before the startup reconcile is refused, and after it the planners run: the worktree planner's
    // crash net assumes the startup sweep has run, and a sweep that runs after it can remove work.
    [Fact]
    public async Task ThePlanners_StartOnlyAfterTheStartupReconcile()
    {
        var backend = CockpitBackend.Build(NullLoggerFactory.Instance);
        await using var services = backend.Services;

        var early = Record.Exception(backend.StartPlanners);
        var watchingBeforeStart = services.GetRequiredService<CiWatcher>().Watching;
        backend.Start();
        backend.StartPlanners();

        Assert.IsType<InvalidOperationException>(early);
        Assert.Null(watchingBeforeStart);
        Assert.NotNull(services.GetRequiredService<CiWatcher>().Watching);
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
