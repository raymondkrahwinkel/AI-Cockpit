using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Profiles;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The TTY panel no longer selects a profile itself (the New-session dialog does, since #31): it is
/// handed the chosen profile and start defaults (permission mode/model/effort) via
/// <see cref="ClaudeTtyViewModel.LaunchConfigured"/> and raises
/// <see cref="ClaudeTtyViewModel.LaunchRequested"/> once the view is subscribed.
/// <see cref="ClaudeTtyViewModel.TryRaiseLaunch"/> bridges the ordering between "launch configured"
/// and "view subscribed"; these tests assert it fires exactly once, whichever happens first.
/// </summary>
public class ClaudeTtyViewModelTests
{
    private static readonly ClaudeProfile Work = new("work", @"C:\Users\raymo\.claude-work");

    [Fact]
    public void LaunchConfigured_WhenAlreadySubscribed_RaisesLaunchWithTheProfileAndOptions()
    {
        ClaudeProfile? launchedProfile = null;
        Guid launchedSessionId = default;
        string? launchedMode = null;
        string? launchedModel = null;
        string? launchedEffort = null;
        var launchCount = 0;
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());
        vm.LaunchRequested += (_, profile, sessionId, mode, model, effort) =>
        {
            launchedProfile = profile;
            launchedSessionId = sessionId;
            launchedMode = mode;
            launchedModel = model;
            launchedEffort = effort;
            launchCount++;
        };

        vm.LaunchConfigured(Work, "acceptEdits", "opus", "high");

        launchCount.Should().Be(1);
        launchedProfile.Should().Be(Work);
        launchedSessionId.Should().NotBe(Guid.Empty);
        launchedMode.Should().Be("acceptEdits");
        launchedModel.Should().Be("opus");
        launchedEffort.Should().Be("high");
        vm.ActiveProfileLabel.Should().Be("work");
        vm.SessionStatus.Should().Be(SessionStatus.Busy);
    }

    [Fact]
    public void LaunchConfigured_EachCall_MintsAFreshSessionId()
    {
        var launchedSessionIds = new List<Guid>();
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());
        vm.LaunchRequested += (_, _, sessionId, _, _, _) => launchedSessionIds.Add(sessionId);

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        launchedSessionIds.Should().ContainSingle().Which.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void LaunchConfigured_BeforeTheViewSubscribes_LaunchesOnTryRaiseLaunch()
    {
        var launchCount = 0;
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");   // configured before any subscriber exists
        vm.LaunchRequested += (_, _, _, _, _, _) => launchCount++;
        launchCount.Should().Be(0);           // nothing raised yet — no subscriber at configure time

        vm.TryRaiseLaunch();                  // the view calls this once it has subscribed

        launchCount.Should().Be(1);
    }

    [Fact]
    public void TryRaiseLaunch_RaisesAtMostOnce()
    {
        var launchCount = 0;
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());
        vm.LaunchRequested += (_, _, _, _, _, _) => launchCount++;

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.TryRaiseLaunch();
        vm.TryRaiseLaunch();

        launchCount.Should().Be(1);
    }

    [Fact]
    public void TryRaiseLaunch_WithoutAConfiguredProfile_DoesNothing()
    {
        var launchCount = 0;
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());
        vm.LaunchRequested += (_, _, _, _, _, _) => launchCount++;

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

    [Fact]
    public void OnLaunchSucceeded_ClearsTheLaunchingStatus()
    {
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>());
        vm.LaunchConfigured(profile: null, permissionMode: null, model: null, effort: null);
        vm.Status.Should().Contain("Launching");

        vm.OnLaunchSucceeded();

        vm.Status.Should().Be("Running");
    }
}
