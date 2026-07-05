using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises the TTY-mode panel's start/profile-selection logic against fakes — the ConPTY spawn is
/// delegated to <see cref="IClaudeTtyLauncher"/>, so the view model is testable without a real pty.
/// The view is what actually invokes the launcher (it owns the terminal size), so these tests assert
/// on the <see cref="ClaudeTtyViewModel.LaunchRequested"/> signal instead.
/// </summary>
public class ClaudeTtyViewModelTests
{
    private static readonly ClaudeProfile Work = new("work", @"C:\Users\raymo\.claude-work");
    private static readonly ClaudeProfile Personal = new("privé", @"C:\Users\raymo\.claude");

    [Fact]
    public async Task Start_WithNoLoggedInProfile_ReportsLoginRequiredAndDoesNotLaunch()
    {
        var launched = false;
        var vm = NewVm([(Work, LoggedIn: false)]);
        vm.LaunchRequested += (_, _) => launched = true;

        await vm.StartCommand.ExecuteAsync(null);

        launched.Should().BeFalse();
        vm.IsLaunched.Should().BeFalse();
        vm.Status.Should().Contain("claude /login");
    }

    [Fact]
    public async Task Start_WithExactlyOneLoggedInProfile_LaunchesSilentlyUnderThatProfile()
    {
        ClaudeProfile? launchedProfile = null;
        var launchCount = 0;
        var vm = NewVm([(Work, LoggedIn: true), (Personal, LoggedIn: false)]);
        vm.LaunchRequested += (_, profile) =>
        {
            launchedProfile = profile;
            launchCount++;
        };

        await vm.StartCommand.ExecuteAsync(null);

        launchCount.Should().Be(1);
        launchedProfile.Should().Be(Work);
        vm.IsLaunched.Should().BeTrue();
        vm.ActiveProfileLabel.Should().Be("work");
        vm.IsChoosingProfile.Should().BeFalse();
    }

    [Fact]
    public async Task Start_WithMoreThanOneLoggedInProfile_AsksBeforeLaunching()
    {
        var launched = false;
        var vm = NewVm([(Work, LoggedIn: true), (Personal, LoggedIn: true)]);
        vm.LaunchRequested += (_, _) => launched = true;

        await vm.StartCommand.ExecuteAsync(null);

        launched.Should().BeFalse();
        vm.IsChoosingProfile.Should().BeTrue();
        vm.ProfileChoices.Should().HaveCount(2);
        vm.SelectedProfile.Should().NotBeNull();
    }

    [Fact]
    public async Task ConfirmProfileChoice_LaunchesUnderTheChosenProfile()
    {
        ClaudeProfile? launchedProfile = null;
        var vm = NewVm([(Work, LoggedIn: true), (Personal, LoggedIn: true)]);
        vm.LaunchRequested += (_, profile) => launchedProfile = profile;

        await vm.StartCommand.ExecuteAsync(null);
        vm.SelectedProfile = Personal;
        vm.ConfirmProfileChoiceCommand.Execute(null);

        launchedProfile.Should().Be(Personal);
        vm.IsChoosingProfile.Should().BeFalse();
        vm.IsLaunched.Should().BeTrue();
    }

    [Fact]
    public void OnProcessExited_MarksTheSessionDone()
    {
        var vm = NewVm([(Work, LoggedIn: true)]);

        vm.OnProcessExited();

        vm.SessionStatus.Should().Be(SessionStatus.Done);
    }

    private static ClaudeTtyViewModel NewVm((ClaudeProfile Profile, bool LoggedIn)[] profiles)
    {
        var launcher = Substitute.For<IClaudeTtyLauncher>();

        var store = Substitute.For<IClaudeProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(profiles.Select(p => p.Profile).ToList());

        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        foreach (var (profile, loggedIn) in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(loggedIn);
        }

        return new ClaudeTtyViewModel(launcher, store, loginChecker);
    }
}
