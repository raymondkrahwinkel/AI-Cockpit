using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Terminal;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The TTY panel no longer selects a profile itself (the New-session dialog does, since #31): it is
/// handed the chosen profile and start defaults (permission mode/model/effort) via
/// <see cref="TtyViewModel.LaunchConfigured"/> and raises
/// <see cref="TtyViewModel.LaunchRequested"/> once the view is subscribed.
/// <see cref="TtyViewModel.TryRaiseLaunch"/> bridges the ordering between "launch configured"
/// and "view subscribed"; these tests assert it fires exactly once, whichever happens first.
/// </summary>
public class TtyViewModelTests
{
    private static readonly SessionProfile Work = new("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work"));

    /// <summary>Resolves any profile (including none) to a fresh provider substitute — same as the real resolver does for a Claude profile or a profile-less session.</summary>
    private static ITtySessionProviderResolver _Resolver()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return resolver;
    }

    [Fact]
    public void LaunchConfigured_WhenAlreadySubscribed_RaisesLaunchWithTheProfileAndOptions()
    {
        SessionProfile? launchedProfile = null;
        IReadOnlyDictionary<string, string>? launchedOptions = null;
        string? launchedWorkingDirectory = null;
        var launchCount = 0;
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());
        vm.LaunchRequested += request =>
        {
            launchedProfile = request.Profile;
            launchedOptions = request.Options;
            launchedWorkingDirectory = request.WorkingDirectory;
            launchCount++;
        };

        vm.LaunchConfigured(Work, "acceptEdits", "opus", "high", "D:/Projects/demo");

        Assert.Equal(1, launchCount);
        Assert.Equal(Work, launchedProfile);
        Assert.NotNull(launchedOptions);
        Assert.Equal("acceptEdits", launchedOptions![TtyLaunchOption.PermissionMode]);
        Assert.Equal("opus", launchedOptions[TtyLaunchOption.Model]);
        Assert.Equal("high", launchedOptions[TtyLaunchOption.Effort]);
        Assert.Equal("D:/Projects/demo", launchedWorkingDirectory);
        Assert.Equal("D:/Projects/demo", vm.WorkingDirectory);
        Assert.Equal("work", vm.ActiveProfileLabel);
        Assert.Equal(SessionStatus.Busy, vm.SessionStatus);
    }

    [Fact]
    public void LaunchConfigured_BeforeTheViewSubscribes_LaunchesOnTryRaiseLaunch()
    {
        var launchCount = 0;
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");   // configured before any subscriber exists
        vm.LaunchRequested += _ => launchCount++;
        Assert.Equal(0, launchCount);           // nothing raised yet — no subscriber at configure time

        vm.TryRaiseLaunch();                  // the view calls this once it has subscribed

        Assert.Equal(1, launchCount);
    }

    [Fact]
    public void LaunchConfigured_LeavesIsTerminalFalse_SoAnAgentSessionIsNeverOfferedToAnotherAgent()
    {
        // IsTerminal is what the pane registers with for the terminal-access MCP (AC-34): true means "a shell the
        // operator opened", and only those are listed, resolvable and couplable. If an agent-CLI session ever came
        // out of this path with it true, its whole transcript would be readable by another agent — so pin it here,
        // at the flag's source, rather than only where it is consumed.
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        Assert.False(vm.IsTerminal);
    }

    [Fact]
    public void LaunchTerminal_SetsIsTerminal_SoAShellTheOperatorOpenedCanBeOffered()
    {
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());

        vm.LaunchTerminal(new ShellDescriptor("pwsh", "PowerShell", "pwsh", []));

        Assert.True(vm.IsTerminal);
    }

    [Fact]
    public void TryRaiseLaunch_RaisesAtMostOnce()
    {
        var launchCount = 0;
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());
        vm.LaunchRequested += _ => launchCount++;

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.TryRaiseLaunch();
        vm.TryRaiseLaunch();

        Assert.Equal(1, launchCount);
    }

    [Fact]
    public void TryRaiseLaunch_WithoutAConfiguredProfile_DoesNothing()
    {
        var launchCount = 0;
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());
        vm.LaunchRequested += _ => launchCount++;

        vm.TryRaiseLaunch();

        Assert.Equal(0, launchCount);
    }

    [Fact]
    public void OnProcessExited_MarksTheSessionDone()
    {
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());

        vm.OnProcessExited();

        Assert.Equal(SessionStatus.Done, vm.SessionStatus);
    }

    [Fact]
    public void OnLaunchSucceeded_ClearsTheLaunchingStatus()
    {
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());
        vm.LaunchConfigured(profile: null, permissionMode: null, model: null, effort: null);
        Assert.Contains("Launching", vm.Status);

        vm.OnLaunchSucceeded();

        Assert.Equal("Running", vm.Status);
    }
}
