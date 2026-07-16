using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using FluentAssertions;
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

        launchCount.Should().Be(1);
        launchedProfile.Should().Be(Work);
        launchedOptions.Should().NotBeNull();
        launchedOptions![TtyLaunchOption.PermissionMode].Should().Be("acceptEdits");
        launchedOptions[TtyLaunchOption.Model].Should().Be("opus");
        launchedOptions[TtyLaunchOption.Effort].Should().Be("high");
        launchedWorkingDirectory.Should().Be("D:/Projects/demo");
        vm.WorkingDirectory.Should().Be("D:/Projects/demo");
        vm.ActiveProfileLabel.Should().Be("work");
        vm.SessionStatus.Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void LaunchConfigured_BeforeTheViewSubscribes_LaunchesOnTryRaiseLaunch()
    {
        var launchCount = 0;
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");   // configured before any subscriber exists
        vm.LaunchRequested += _ => launchCount++;
        launchCount.Should().Be(0);           // nothing raised yet — no subscriber at configure time

        vm.TryRaiseLaunch();                  // the view calls this once it has subscribed

        launchCount.Should().Be(1);
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

        launchCount.Should().Be(1);
    }

    [Fact]
    public void TryRaiseLaunch_WithoutAConfiguredProfile_DoesNothing()
    {
        var launchCount = 0;
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());
        vm.LaunchRequested += _ => launchCount++;

        vm.TryRaiseLaunch();

        launchCount.Should().Be(0);
    }

    [Fact]
    public void OnProcessExited_MarksTheSessionDone()
    {
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());

        vm.OnProcessExited();

        vm.SessionStatus.Should().Be(SessionStatus.Done);
    }

    [Fact]
    public void OnLaunchSucceeded_ClearsTheLaunchingStatus()
    {
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());
        vm.LaunchConfigured(profile: null, permissionMode: null, model: null, effort: null);
        vm.Status.Should().Contain("Launching");

        vm.OnLaunchSucceeded();

        vm.Status.Should().Be("Running");
    }
}
