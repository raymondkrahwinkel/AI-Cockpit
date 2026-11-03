using System.Reflection;
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
    public void NeitherToolTakesAProjectOrAPathFromItsCaller()
    {
        // The point of the whole design: a session cannot ask for a run in another session's tree, and a
        // prompt-injected one cannot be talked into naming somewhere else. Pinned on the signature, because that is
        // where such a parameter would quietly reappear.
        foreach (var tool in typeof(LocalCiMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var names = tool.GetParameters().Select(parameter => parameter.Name!).ToList();

            Assert.DoesNotContain(names, name => name.Contains("path", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("project", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("directory", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("session", StringComparison.OrdinalIgnoreCase));
        }
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

    // Registered to a different pane — the exact confused-deputy case worktree_remove already guards against — or
    // not any registered worktree at all.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACheckoutTheCallerDoesNotOwnIsRefused(bool registeredToAnotherPane)
    {
        _host.CallerPaneId = "pane-caller";
        var worktrees = new FakeWorktreeManager();
        if (registeredToAnotherPane)
        {
            worktrees.Records.Add(new WorktreeRecord("pane-neighbour", _neighbour.Root, _neighbour.Root, "cockpit/other", "abc123", DateTimeOffset.UtcNow));
        }

        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act, worktrees: worktrees).RunLocalChecks(checkout: _neighbour.Root);

        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    // The blank is the interesting one: a host that has no answer and a host that answers with nothing look alike
    // here, and treating the second as a real caller is how a lookup starts being done on an id nobody vouched for.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutAKnownCallerNothingRuns(string? callerPaneId)
    {
        _host.CallerPaneId = callerPaneId;
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act).RunLocalChecks();

        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task UnknownAndDirectorylessCallersGetDifferentErrors()
    {
        _host.CallerPaneId = "pane-that-never-registered";
        var act = FakeStreamingCliRunner.Exiting(0);

        var unknown = await _Tools(act).RunLocalChecks();
        _host.CallerPaneId = "pane-without-checkout";
        _host.SessionObserver.Open.Add(new OpenCockpitSession("pane-without-checkout", "No checkout"));
        _host.SessionObserver.WorkingDirectories["pane-without-checkout"] = string.Empty;
        _checkouts.Remember(new FakeSession("pane-without-checkout", _neighbour.Root));
        var directoryless = await _Tools(act).RunLocalChecks();

        Assert.Contains("does not know the pane", _Read(unknown).GetProperty("error").GetString());
        Assert.Contains("has no working directory or checkout", _Read(directoryless).GetProperty("error").GetString());
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

    [Fact]
    public async Task SkipConsentRunsWithoutAskingTheOperator()
    {
        // AC-710: with the operator's opt-out on, the run goes straight through — no RequestConsentAsync round-trip.
        _host.CallerPaneId = "pane-caller";
        var act = FakeStreamingCliRunner.Exiting(0);

        await _Tools(act, skipConsent: true).RunLocalChecks();
        var verdict = _Read(await _host.Delivered);

        Assert.Empty(_host.Asked);
        Assert.Single(act.Calls);
        Assert.True(verdict.GetProperty("ok").GetBoolean());
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
