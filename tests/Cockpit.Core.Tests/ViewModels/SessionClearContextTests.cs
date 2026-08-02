using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-564 — clearing an SDK session's context. The restart itself is the whole feature: the same panel starts a
/// second conversation with no <see cref="SessionResume"/>, which is what makes the agent know nothing. These
/// drive the real <see cref="SessionViewModel.ClearContextAsync"/> against a fake driver, so what is asserted is
/// what the driver is actually asked for — not what the view model believes it asked for.
/// </summary>
public class SessionClearContextTests
{
    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    [Fact]
    public async Task ClearContext_RestartsTheSession_WithoutResumingTheOldConversation()
    {
        var (vm, driver) = await StartedVm(SessionResume.BySessionId("conv-old"));

        await vm.ClearContextAsync(Profile);

        // Started again — and the second start carries no resume, which is the entire difference between
        // clearing the context and picking the earlier conversation back up.
        await driver.Received(1).StartAsync(
            Profile, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
            SessionResume.BySessionId("conv-old"), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await driver.Received(1).StartAsync(
            Profile, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
            null, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ClearContext_KeepsTheSessionsOwnIdentity()
    {
        var (vm, _) = await StartedVm();
        vm.Title = "release check";
        vm.WorkingDirectory = @"C:\work\cockpit";
        vm.ProjectId = "project-7";
        var paneId = vm.PaneId;

        await vm.ClearContextAsync(Profile);

        // The pane is the same pane: its name, its id (which its statusline, audit entries and scheduled resumes
        // all key on), its place and its working directory come through untouched. Closing and reopening — the
        // shortcut this ticket exists to avoid — is exactly what loses these.
        Assert.Equal(paneId, vm.PaneId);
        Assert.Equal("release check", vm.Title);
        Assert.Equal(@"C:\work\cockpit", vm.WorkingDirectory);
        Assert.Equal("project-7", vm.ProjectId);
        Assert.Equal(Profile.Label, vm.ActiveProfileLabel);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ClearContext_RestartsInTheDirectoryTheSessionIsAlreadyIn()
    {
        var (vm, driver) = await StartedVm(workingDirectory: @"C:\work\cockpit-wt-3");

        await vm.ClearContextAsync(Profile);

        // Both starts name the same directory. A session isolated in a worktree must not be handed the project
        // folder again on the restart — that is how a second worktree would appear beside the first.
        await driver.Received(2).StartAsync(
            Profile, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), @"C:\work\cockpit-wt-3",
            Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ClearContext_KeepsTheTranscript_AndMarksWhereTheMemoryStops()
    {
        var (vm, _) = await StartedVm();
        vm.Apply(new AssistantTextDelta { SessionId = "S1", Text = "the old answer", BlockIndex = 0 });

        await vm.ClearContextAsync(Profile);

        Assert.Contains(vm.Transcript, entry => entry.Text.Contains("the old answer"));
        var divider = Assert.Single(vm.Transcript, entry => entry.IsDivider);
        Assert.Contains("Context cleared", divider.Text);
        // A divider explains everything below it, so no reading level may hide it.
        Assert.True(divider.IsRowVisible);
        Assert.False(divider.IsPlainText);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ClearContext_ResetsTheContextFigureAndTheTokenMeter()
    {
        var (vm, driver) = await StartedVm();
        driver.CurrentStatus.Returns(new SessionStatusFeed(66, [new SessionRateWindow("5h", 40, null)]));
        vm.Apply(new TurnCompleted
        {
            SessionId = "S1", Subtype = "success", Result = "done", IsError = false,
            Usage = new TokenUsage(1_000, 2_000, 0, 0), TotalCostUsd = 0.05,
        });
        Assert.Equal(66, vm.ContextUsedPercent);
        Assert.True(vm.HasUsage);

        await vm.ClearContextAsync(Profile);

        // "ctx 66%" over a conversation that has no context is a number that actively lies, and the token total
        // belongs to the process that ran it up (decision 3).
        Assert.Null(vm.ContextUsedPercent);
        Assert.Empty(vm.RateLimits);
        Assert.Empty(vm.LimitsTooltip);
        Assert.False(vm.HasUsage);
        Assert.Empty(vm.UsageSummary);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ClearContext_DuringAToolCallAwaitingPermission_LeavesNothingHalfRunning()
    {
        var (vm, driver) = await StartedVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{\"command\":\"ls\"}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{\"command\":\"ls\"}" });
        Assert.True(vm.HasPendingPermission);
        Assert.True(vm.HasActiveToolActivity);

        await vm.ClearContextAsync(Profile);

        // The prompt is answered rather than abandoned: the driver that asked is gone, so a row left pending
        // would keep the pane asking for attention over a decision nothing is waiting for anymore.
        Assert.False(vm.HasPendingPermission);
        Assert.False(vm.HasActiveToolActivity);
        Assert.False(vm.IsBusy);
        Assert.Equal("Cancelled — context cleared", vm.Transcript.Single(entry => entry.ToolUseId == "t1").PermissionDecision);
        // And it did restart: a cleared session is a usable session, not a stopped one.
        await driver.Received(2).StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
            Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ClearContext_DuringARunningTurn_InterruptsItAndDropsWhatWasQueuedBehindIt()
    {
        var (vm, driver) = await StartedVm();
        vm.InputText = "start the build";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "and then run the tests";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.True(vm.HasQueuedMessages);

        await vm.ClearContextAsync(Profile);

        // The turn is interrupted rather than having its process pulled from under it, and a message queued
        // behind it was aimed at the conversation that just ended — it would otherwise sit in the strip waiting
        // for a turn boundary the new conversation has no reason to reach.
        await driver.Received().InterruptAsync(Arg.Any<CancellationToken>());
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasQueuedMessages);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ClearContext_OnASessionThatNeverStarted_DoesNothing()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(driver)));

        await vm.ClearContextAsync(Profile);

        await driver.DidNotReceive().StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
            Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Empty(vm.Transcript);

        await vm.DisposeAsync();
    }

    [Fact]
    public void OnlyTheSdkPaneOffersTheAction()
    {
        // Criterion 1: a TTY session is a real TUI, where the operator types /clear.
        Assert.True(NewVm().SupportsClearContext);
        Assert.False(new TtyViewModel().SupportsClearContext);
    }

    private static SessionViewModel NewVm()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(EmptyEvents());
        return new SessionViewModel(new SessionManager(FactoryFor(driver)));
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Driver)> StartedVm(
        SessionResume? resume = null, string? workingDirectory = null)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(driver)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort, workingDirectory: workingDirectory, resume: resume);
        return (vm, driver);
    }

    private static async IAsyncEnumerable<SessionEvent> EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static ISessionDriverFactory FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
