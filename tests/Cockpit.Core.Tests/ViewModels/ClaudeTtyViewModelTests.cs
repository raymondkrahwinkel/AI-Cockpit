using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Profiles;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The TTY panel no longer selects a profile itself (the New-session dialog does, since #31): it is
/// handed the chosen profile via <see cref="ClaudeTtyViewModel.LaunchConfigured"/> and raises
/// <see cref="ClaudeTtyViewModel.LaunchRequested"/> once the view is subscribed.
/// <see cref="ClaudeTtyViewModel.TryRaiseLaunch"/> bridges the ordering between "profile configured"
/// and "view subscribed"; these tests assert it fires exactly once, whichever happens first.
/// </summary>
public class ClaudeTtyViewModelTests
{
    private static readonly ClaudeProfile Work = new("work", @"C:\Users\raymo\.claude-work");

    [Fact]
    public void LaunchConfigured_WhenAlreadySubscribed_RaisesLaunchWithTheProfile()
    {
        ClaudeProfile? launchedProfile = null;
        var launchCount = 0;
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());
        vm.LaunchRequested += (_, profile) =>
        {
            launchedProfile = profile;
            launchCount++;
        };

        vm.LaunchConfigured(Work);

        launchCount.Should().Be(1);
        launchedProfile.Should().Be(Work);
        vm.ActiveProfileLabel.Should().Be("work");
        vm.SessionStatus.Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void LaunchConfigured_BeforeTheViewSubscribes_LaunchesOnTryRaiseLaunch()
    {
        var launchCount = 0;
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());

        vm.LaunchConfigured(Work);            // configured before any subscriber exists
        vm.LaunchRequested += (_, _) => launchCount++;
        launchCount.Should().Be(0);           // nothing raised yet — no subscriber at configure time

        vm.TryRaiseLaunch();                  // the view calls this once it has subscribed

        launchCount.Should().Be(1);
    }

    [Fact]
    public void TryRaiseLaunch_RaisesAtMostOnce()
    {
        var launchCount = 0;
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());
        vm.LaunchRequested += (_, _) => launchCount++;

        vm.LaunchConfigured(Work);
        vm.TryRaiseLaunch();
        vm.TryRaiseLaunch();

        launchCount.Should().Be(1);
    }

    [Fact]
    public void TryRaiseLaunch_WithoutAConfiguredProfile_DoesNothing()
    {
        var launchCount = 0;
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());
        vm.LaunchRequested += (_, _) => launchCount++;

        vm.TryRaiseLaunch();

        launchCount.Should().Be(0);
    }

    [Fact]
    public void OnProcessExited_MarksTheSessionDone()
    {
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());

        vm.OnProcessExited();

        vm.SessionStatus.Should().Be(SessionStatus.Done);
    }
}
