using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>AC-713: reactive and proactive login prompts both land the operator in the same inline <see cref="LoginFlowRowViewModel"/>.</summary>
public class SessionViewModelLoginTests
{
    [Fact]
    public async Task SessionError_WhenTheProfileIsLoggedOut_GrowsALoginAction()
    {
        var (vm, _) = await _StartedAsync(loginChecker: FakeChecker(isLoggedIn: false));

        vm.Apply(new SessionError { SessionId = "S1", Message = "401 Unauthorized" });
        var entry = vm.Transcript.Single(row => row.Kind == TranscriptEntryKind.Error);

        Assert.True(entry.HasAction);
        Assert.Equal("Login", entry.ActionLabel);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionError_WhenTheProfileIsStillLoggedIn_GrowsNoAction()
    {
        var (vm, _) = await _StartedAsync(loginChecker: FakeChecker(isLoggedIn: true));

        vm.Apply(new SessionError { SessionId = "S1", Message = "the tool crashed" });
        var entry = vm.Transcript.Single(row => row.Kind == TranscriptEntryKind.Error);

        Assert.False(entry.HasAction, "an ordinary failure on a still-logged-in profile is not an auth error");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionError_WithNoLoginCheckerWired_GrowsNoAction()
    {
        // Design-time/most-tests shape: no login checker at all — must not throw, and the row stays a plain error.
        var (vm, _) = await _StartedAsync(loginChecker: null);

        vm.Apply(new SessionError { SessionId = "S1", Message = "boom" });
        var entry = vm.Transcript.Single(row => row.Kind == TranscriptEntryKind.Error);

        Assert.False(entry.HasAction);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TheLoginAction_StartsTheFlowInlineOnTheSameRow()
    {
        var flow = _FakeFlow();
        var starter = Substitute.For<IProfileLoginStarter>();
        starter.StartLogin(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>()).Returns(flow);
        var (vm, _) = await _StartedAsync(loginChecker: FakeChecker(isLoggedIn: false), loginStarter: starter);

        vm.Apply(new SessionError { SessionId = "S1", Message = "401 Unauthorized" });
        var entry = vm.Transcript.Single(row => row.Kind == TranscriptEntryKind.Error);

        entry.ActionCommand?.Execute(null);

        Assert.NotNull(entry.LoginFlow);
        Assert.False(entry.HasAction, "the button hides once the flow has taken its place");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ReportLoginStatus_LoggedOut_RaisesTheAuthExpiryWarningWithASignInAgainButton()
    {
        var (vm, _) = await _StartedAsync();

        vm.ReportLoginStatus(isLoggedIn: false);

        var warning = Assert.Single(vm.Warnings);
        Assert.Equal("cockpit.auth-expiry", warning.Key);
        Assert.True(warning.ShowSignInAgain);
        Assert.Contains("expire", warning.Text, StringComparison.OrdinalIgnoreCase);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ReportLoginStatus_BackToLoggedIn_ClearsTheWarning()
    {
        var (vm, _) = await _StartedAsync();
        vm.ReportLoginStatus(isLoggedIn: false);

        vm.ReportLoginStatus(isLoggedIn: true);

        Assert.Empty(vm.Warnings);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SignInAgain_OpensAFreshTranscriptRowAndStartsTheFlowThere()
    {
        var flow = _FakeFlow();
        var starter = Substitute.For<IProfileLoginStarter>();
        starter.StartLogin(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>()).Returns(flow);
        var (vm, _) = await _StartedAsync(loginStarter: starter);

        vm.SignInAgainCommand.Execute(null);

        var entry = Assert.Single(vm.Transcript);
        Assert.NotNull(entry.LoginFlow);
        // AC-720: not an Error row — this is a status line, not a driver failure, and Error rows now
        // render as a severity-coloured card that would misread "Signing in again…" as a problem.
        Assert.False(entry.IsErrorRow);

        await vm.DisposeAsync();
    }

    private static IProfileLoginChecker FakeChecker(bool isLoggedIn)
    {
        var checker = Substitute.For<IProfileLoginChecker>();
        checker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(isLoggedIn);
        return checker;
    }

    private static ILoginFlow _FakeFlow()
    {
        var flow = Substitute.For<ILoginFlow>();
        flow.Steps.Returns(_EmptySteps());
        flow.Completion.Returns(Task.FromResult(new LoginFlowResult(Success: true, ErrorMessage: null)));
        return flow;
    }

    private static async IAsyncEnumerable<LoginFlowStep> _EmptySteps([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Driver)> _StartedAsync(
        IProfileLoginChecker? loginChecker = null, IProfileLoginStarter? loginStarter = null)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyEvents());
        driver.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));

        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var vm = new SessionViewModel(new SessionManager(factory), loginChecker: loginChecker, loginStarter: loginStarter);
        await vm.StartConfiguredAsync(
            new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
            SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        return (vm, driver);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
