using Cockpit.Core.Abstractions.Verify;
using Cockpit.Core.Verify;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Verify;
using Cockpit.Plugins.Abstractions.Consent;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Verify;

/// <summary>
/// The <c>verify</c> MCP tool (AC-86). The command it runs comes from the registry, never the agent, and only after
/// the operator approves; the snapshot rides back on the tool result while the screenshot goes through the session
/// feed. These prove it runs exactly the registered command, keys the target on the pane the request authenticated
/// as (refusing a request that carries none), and does nothing when there is no runner or the operator declines.
/// </summary>
public sealed class VerifyMcpToolsTests : IDisposable
{
    private const string Session = "pane-1";

    private readonly string _projectDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
    private readonly IVerifyRunnerRegistry _registry = Substitute.For<IVerifyRunnerRegistry>();
    private readonly IVerifySessionGateway _gateway = Substitute.For<IVerifySessionGateway>();
    private readonly IVerifyCommandRunner _commandRunner = Substitute.For<IVerifyCommandRunner>();
    private readonly IConsentBroker _consent = Substitute.For<IConsentBroker>();

    public VerifyMcpToolsTests()
    {
        Directory.CreateDirectory(_projectDir);
        _gateway.GetWorkingDirectory(Session).Returns(_projectDir);
        _gateway.FeedResultAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(true);
        _Approve();
        _commandRunner.RunAsync(Arg.Any<VerifyRunner>(), Arg.Any<CancellationToken>())
            .Returns(new VerifyRunResult(0, string.Empty, string.Empty, TimeSpan.FromSeconds(1), TimedOut: false));
    }

    [Fact]
    public async Task Verify_RunsTheRegisteredCommand_ReturnsTheSnapshot_AndFeedsTheScreenshot()
    {
        var runner = _RegisterRunner(withScreenshot: true);
        _RunProducesSnapshot(runner, "Border bg=#131519 corner=20\n  TextBlock text=Session 1", [0x89, 0x50, 0x4E, 0x47]);

        var response = await _ToolFor(Session).VerifyAsync();

        // It ran exactly the runner the registry holds — the tool has no other command to run.
        await _commandRunner.Received(1).RunAsync(runner, Arg.Any<CancellationToken>());
        // The snapshot text comes back on the tool result (not injected anywhere).
        response.Should().Contain("Session 1").And.Contain("\"ok\":true");
        // The screenshot — which a tool result cannot carry — goes through the feed as an image.
        await _gateway.Received(1).FeedResultAsync(
            Session,
            Arg.Any<string>(),
            Arg.Is<byte[]>(bytes => bytes.Length == 4),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_KeysOnThePaneTheRequestAuthenticatedAs()
    {
        var runner = _RegisterRunner(withScreenshot: true);
        _RunProducesSnapshot(runner, "Border bg=#131519", [0x89, 0x50, 0x4E, 0x47]);
        const string authenticatedPane = "verified-pane";
        _gateway.GetWorkingDirectory(authenticatedPane).Returns(_projectDir);

        var response = await _ToolFor(authenticatedPane).VerifyAsync();

        response.Should().Contain("\"ok\":true");
        // Selection and feed key on the authenticated pane — the agent has no say over which pane it targets.
        _gateway.Received().GetWorkingDirectory(authenticatedPane);
        await _gateway.Received(1).FeedResultAsync(authenticatedPane, Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_WithoutAnAuthenticatedPane_RefusesAndRunsNothing()
    {
        _RegisterRunner(withScreenshot: false);

        var response = await _ToolFor(null).VerifyAsync();

        response.Should().Contain("\"ok\":false");
        await _commandRunner.DidNotReceive().RunAsync(Arg.Any<VerifyRunner>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_ClearsStaleOutputBeforeRunning_SoAFailedRunCannotReportTheOldUi()
    {
        var runner = _RegisterRunner(withScreenshot: false);
        // A snapshot from a previous run sits on disk; this run's command (the mock) writes nothing new.
        await File.WriteAllTextAsync(runner.SnapshotPath, "STALE Border bg=#000000");

        var response = await _ToolFor(Session).VerifyAsync();

        // The stale file is cleared before the run, so with nothing fresh written the tool reports failure —
        // it never vouches for the old UI as ok:true.
        response.Should().Contain("\"ok\":false").And.Contain("no snapshot");
        response.Should().NotContain("STALE");
    }

    [Fact]
    public async Task Verify_IgnoresASnapshotNotWrittenByThisRun()
    {
        var runner = _RegisterRunner(withScreenshot: false);
        // A leftover the run did not refresh (a delete the OS refused): the file is present at read time but its
        // last-write is before the run started, so the freshness gate must treat it as absent, not vouch for it.
        _commandRunner.RunAsync(runner, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            File.WriteAllText(runner.SnapshotPath, "STALE Border bg=#000000");
            File.SetLastWriteTimeUtc(runner.SnapshotPath, DateTime.UtcNow.AddMinutes(-5));
            return new VerifyRunResult(0, string.Empty, string.Empty, TimeSpan.FromSeconds(1), TimedOut: false);
        });

        var response = await _ToolFor(Session).VerifyAsync();

        response.Should().Contain("\"ok\":false").And.Contain("no snapshot");
        response.Should().NotContain("STALE");
    }

    [Fact]
    public async Task Verify_ResolvesARelativeSnapshotPath_AgainstTheProjectDirectory()
    {
        var runner = new VerifyRunner(
            Label: "Cockpit", WorkingDirectory: _projectDir, Command: "dotnet",
            Arguments: ["run"], SnapshotPath: "out.txt", ScreenshotPath: null);
        _registry.ListAsync(Arg.Any<CancellationToken>()).Returns([runner]);
        // The command runs in the project directory and writes the snapshot there under the relative name.
        _commandRunner.RunAsync(runner, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            File.WriteAllText(Path.Combine(_projectDir, "out.txt"), "Border bg=#131519");
            return new VerifyRunResult(0, string.Empty, string.Empty, TimeSpan.FromSeconds(1), TimedOut: false);
        });

        var response = await _ToolFor(Session).VerifyAsync();

        // Read against the project directory, not the cockpit process's own — the snapshot is found and returned.
        response.Should().Contain("\"ok\":true").And.Contain("#131519");
    }

    [Fact]
    public async Task Verify_WithNoRegisteredRunner_RunsNothing()
    {
        _registry.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<VerifyRunner>());

        var response = await _ToolFor(Session).VerifyAsync();

        response.Should().Contain("\"ok\":false");
        await _commandRunner.DidNotReceive().RunAsync(Arg.Any<VerifyRunner>(), Arg.Any<CancellationToken>());
        await _gateway.DidNotReceive().FeedResultAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_WhenTheOperatorDeclines_RunsNothing()
    {
        _RegisterRunner(withScreenshot: false);
        _consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(ConsentDecision.Denied);

        var response = await _ToolFor(Session).VerifyAsync();

        response.Should().Contain("\"ok\":false");
        await _commandRunner.DidNotReceive().RunAsync(Arg.Any<VerifyRunner>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_WhenTheCommandProducesNoSnapshot_ReportsFailureWithoutFeeding()
    {
        _RegisterRunner(withScreenshot: false); // its SnapshotPath is never written

        var response = await _ToolFor(Session).VerifyAsync();

        response.Should().Contain("\"ok\":false").And.Contain("no snapshot");
        await _gateway.DidNotReceive().FeedResultAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    // Models a real command writing its output when it runs, so the tool reads freshly-produced files — not the
    // stale ones it deletes beforehand.
    private void _RunProducesSnapshot(VerifyRunner runner, string snapshot, byte[]? screenshot = null) =>
        _commandRunner.RunAsync(runner, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            File.WriteAllText(runner.SnapshotPath, snapshot);
            if (screenshot is not null && runner.ScreenshotPath is not null)
            {
                File.WriteAllBytes(runner.ScreenshotPath, screenshot);
            }

            return new VerifyRunResult(0, string.Empty, string.Empty, TimeSpan.FromSeconds(1), TimedOut: false);
        });

    // Sets the authenticated pane the middleware would stamp onto the request, then builds the tool — so a test drives
    // it as the pane it authenticated as, exactly how a real per-session-token request arrives (AC-89).
    private VerifyMcpTools _ToolFor(string? authenticatedPane)
    {
        McpRequestContext.Set(authenticatedPane);
        return new VerifyMcpTools(_registry, _gateway, _commandRunner, _consent);
    }

    private void _Approve() =>
        _consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(ConsentOutcome.Approved));

    private VerifyRunner _RegisterRunner(bool withScreenshot)
    {
        var runner = new VerifyRunner(
            Label: "Cockpit",
            WorkingDirectory: _projectDir,
            Command: "dotnet",
            Arguments: ["run", "--", "--snapshot", "out.txt"],
            SnapshotPath: Path.Combine(_projectDir, "out.txt"),
            ScreenshotPath: withScreenshot ? Path.Combine(_projectDir, "out.png") : null);
        _registry.ListAsync(Arg.Any<CancellationToken>()).Returns([runner]);
        return runner;
    }

    public void Dispose()
    {
        McpRequestContext.Set(null);
        if (Directory.Exists(_projectDir))
        {
            Directory.Delete(_projectDir, recursive: true);
        }
    }
}
