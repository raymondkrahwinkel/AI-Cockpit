using System.Text.Json;
using Cockpit.Core.Worktrees;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Mcp;
using Cockpit.Plugin.LocalCi.Sessions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.LocalCi.Tests;

public class LocalCiMcpToolsTests : IDisposable
{
    private readonly TemporaryProject _caller = new();
    private readonly TemporaryProject _neighbour = new();
    private readonly FakeCockpitHost _host = new();
    private readonly SessionCheckouts _checkouts = new();
    private readonly LocalRunTracker _tracker = new();

    public LocalCiMcpToolsTests()
    {
        _caller.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);
        _neighbour.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);
        _checkouts.Remember(new FakeSession("pane-caller", _caller.Root));
        _checkouts.Remember(new FakeSession("pane-neighbour", _neighbour.Root));
    }

    public void Dispose()
    {
        _caller.Dispose();
        _neighbour.Dispose();
    }

    [Fact]
    public async Task TheRunHappensInTheCallersCheckoutAndNotAnybodyElses()
    {
        _host.CallerPaneId = "pane-caller";
        _checkouts.Forget("pane-caller");
        _host.SessionObserver.Open.Add(new OpenCockpitSession("pane-caller", "Caller"));
        _host.SessionObserver.WorkingDirectories["pane-caller"] = _caller.Root;
        var act = FakeStreamingCliRunner.Exiting(0);

        var status = await _Tools(act).LocalCheckStatus();
        await _Tools(act).RunLocalChecks();
        await _host.Delivered;

        Assert.Equal(_caller.Root, _Read(status).GetProperty("checkout").GetString());
        Assert.Equal(_caller.Root, act.Calls.Single().WorkingDirectory);
    }

    [Fact]
    public async Task ACheckoutTheCallerRegisteredForItselfViaWorktreeCreateCanBeTested()
    {
        // AC-1015: a subtask worktree an agent made for itself never gets its own pane, so it can only be reached
        // by naming it — but only because it is registered to this same caller.
        _host.CallerPaneId = "pane-caller";
        var worktrees = new FakeWorktreeManager();
        worktrees.Records.Add(new WorktreeRecord("pane-caller", _caller.Root, _neighbour.Root, "cockpit/subtask", "abc123", DateTimeOffset.UtcNow));
        var act = FakeStreamingCliRunner.Exiting(0);

        await _Tools(act, worktrees: worktrees).RunLocalChecks(checkout: _neighbour.Root);
        await _host.Delivered;

        Assert.Equal(_neighbour.Root, act.Calls.Single().WorkingDirectory);
    }

    [Fact]
    public async Task ACheckoutTheCallerDoesNotOwnIsRefused()
    {
        _host.CallerPaneId = "pane-caller";
        // Registered to a different pane — the exact confused-deputy case worktree_remove already guards against.
        var worktrees = new FakeWorktreeManager();
        worktrees.Records.Add(new WorktreeRecord("pane-neighbour", _neighbour.Root, _neighbour.Root, "cockpit/other", "abc123", DateTimeOffset.UtcNow));
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act, worktrees: worktrees).RunLocalChecks(checkout: _neighbour.Root);

        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task ACheckoutThatIsNotAnyRegisteredWorktreeIsRefused()
    {
        _host.CallerPaneId = "pane-caller";
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act, worktrees: new FakeWorktreeManager()).RunLocalChecks(checkout: _neighbour.Root);

        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task WithoutAKnownCallerNothingRuns()
    {
        _host.CallerPaneId = null;
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act).RunLocalChecks();

        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task TheOperatorIsAskedWithTheLiteralCommandAndDenyingItStopsTheRun()
    {
        _host.CallerPaneId = "pane-caller";
        _host.Answer = ConsentOutcome.Denied;
        var act = FakeStreamingCliRunner.Exiting(0);

        await _Tools(act).RunLocalChecks();
        var verdict = await _host.Delivered;

        var asked = Assert.Single(_host.Asked);
        Assert.StartsWith("act ", asked.Action);
        Assert.Contains(_caller.Root, asked.Action);
        Assert.Equal(ConsentRisk.Dangerous, asked.Risk);
        Assert.Empty(act.Calls);
        Assert.Equal(nameof(LocalRunOutcome.NotApproved), _Read(verdict).GetProperty("verdict").GetString());
    }

    private LocalCiMcpTools _Tools(IStreamingCliRunner act, bool skipConsent = false, FakeWorktreeManager? worktrees = null)
    {
        var runner = new LocalJobRunner(
            FakeLocalCiRuntime.Ready(), act, new FakeRunContainerCleanup(), () => ActRunOptions.For(8), () => "run-1");
        var settings = new LocalCiSettings(_host.Storage) { SkipConsent = skipConsent };

        return new LocalCiMcpTools(_host, _checkouts, runner, _tracker, new GitHead(new FakeCliRunner()), settings, worktrees);
    }

    private static JsonElement _Read(string json) => JsonDocument.Parse(json).RootElement;
}
