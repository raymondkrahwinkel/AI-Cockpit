using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cockpit.App;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Services;

// AC-1124 finding, closed by AC-1134. Measured in the running Debug app against a real Claude CLI child process
// (real MCP fan-out, real driver): one session.DisposeAsync() took 530ms — squarely in the range where a single
// slow pane starves every pane behind it under the old serial loop. The loop under test is copied verbatim from
// CockpitViewModel.DisposeAsync (now parallel, AC-1134); the runtimes, the budget wrapper
// (Program.AwaitTeardownAsync) and the real TeardownBudget value are production code.
public class Ac1124_ShutdownTeardownBudgetTests
{
    // The real value: Program.cs `TeardownBudget = TimeSpan.FromSeconds(3)`. Named here because the field is
    // private; if it moves, this test is measuring the wrong thing and should be updated with it.
    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(3);

    // CockpitViewModel.DisposeAsync's pane loop — parallel, no shared state between panes requires an order.
    private static Task _TearDownPanesAsync(IReadOnlyList<SessionRuntime> sessions) =>
        Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));

    private static async Task<(List<_FakeDriver> Drivers, List<SessionRuntime> Runtimes)> _PanesAsync(
        int count, Func<int, TimeSpan?> interruptCost, Func<int, TimeSpan> disposeCost)
    {
        var drivers = new List<_FakeDriver>();
        var runtimes = new List<SessionRuntime>();
        for (var i = 0; i < count; i++)
        {
            var driver = new _FakeDriver(interruptCost(i), disposeCost(i));
            var runtime = new SessionRuntime(new _FakeFactory(driver), profile: null);
            await runtime.StartAsync(profile: null);
            drivers.Add(driver);
            runtimes.Add(runtime);
        }

        return (drivers, runtimes);
    }

    // POSITIVE CONTROL. Same loop, same real budget, panes that answer quickly: every driver is disposed and
    // the budget is not reached. If this failed, the fixes below would be an artefact of the harness.
    [Fact]
    public async Task PositiveControl_PanesThatAnswerQuickly_AreAllTornDownInsideTheRealBudget()
    {
        var (drivers, runtimes) = await _PanesAsync(6, _ => TimeSpan.FromMilliseconds(50), _ => TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        await Program.AwaitTeardownAsync(_TearDownPanesAsync(runtimes), TeardownBudget);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TeardownBudget, $"took {stopwatch.ElapsedMilliseconds}ms");
        Assert.All(drivers, driver => Assert.True(driver.Disposed));
    }

    // Pane 0's InterruptAsync never returns — a CLI that stopped answering its control protocol.
    // SessionRuntime.DisposeAsync now bounds that wait with InterruptGrace (AC-1134), so pane 0 unblocks and every
    // pane — including pane 0 itself — is still disposed inside the budget. Before the fix, this was 0 of 6.
    [Fact]
    public async Task OneUnresponsivePane_NoLongerStarvesAnyPane()
    {
        var (drivers, runtimes) = await _PanesAsync(
            6,
            index => index == 0 ? null : TimeSpan.Zero,
            _ => TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        await Program.AwaitTeardownAsync(_TearDownPanesAsync(runtimes), TeardownBudget);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TeardownBudget, $"took {stopwatch.ElapsedMilliseconds}ms");
        Assert.All(drivers, driver => Assert.True(driver.Disposed));
    }

    // Nothing is wedged on interrupt; each pane's own teardown (driver.DisposeAsync — killing its real child
    // process, AC-1134's 530ms measurement) costs more than the budget divided by their number. Serially, 8 * 600ms
    // is 4.8s against a 3s budget and the tail is abandoned; in parallel (AC-1134) the wait is the slowest pane,
    // not their sum, so nothing is abandoned.
    [Fact]
    public async Task PanesThatCostMoreThanTheBudget_AreAllStillTornDownInParallel()
    {
        var (drivers, runtimes) = await _PanesAsync(8, _ => TimeSpan.Zero, _ => TimeSpan.FromMilliseconds(600));

        await Program.AwaitTeardownAsync(_TearDownPanesAsync(runtimes), TeardownBudget);

        var abandoned = drivers.Count(driver => !driver.Disposed);
        Assert.Equal(0, abandoned);
    }

    private sealed class _FakeFactory(ISessionDriver driver) : ISessionDriverFactory
    {
        public ISessionDriver Create(SessionProfile? profile) => driver;
    }

    // Stands in for a provider driver. `interruptCost` null = never answers (SessionRuntime.DisposeAsync bounds
    // this with InterruptGrace, AC-1134); `disposeCost` stands in for the real cost of tearing down a live child
    // process, measured at 530ms against a real Claude CLI.
    private sealed class _FakeDriver(TimeSpan? interruptCost, TimeSpan disposeCost) : ISessionDriver
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public SessionCapabilities Capabilities => new(false, false, false, false, false, false, false, false);

        public string? SessionId => "conversation-1";

        public SessionProfile? Profile => null;

        public IAsyncEnumerable<SessionEvent> Events => _StreamAsync();

        private async IAsyncEnumerable<SessionEvent> _StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default) =>
            interruptCost is { } cost ? Task.Delay(cost, CancellationToken.None) : _never.Task;

        public Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (disposeCost > TimeSpan.Zero)
            {
                await Task.Delay(disposeCost);
            }

            Disposed = true;
        }
    }
}
