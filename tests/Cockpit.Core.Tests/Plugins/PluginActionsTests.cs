using Avalonia.Input.Platform;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using FluentAssertions;
using NSubstitute;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The cockpit actions a plugin can perform (#14): inject into the active session, put text on the clipboard.</summary>
public class PluginActionsTests
{
    [Fact]
    public void HasActiveSession_ReflectsTheSelectedSession()
    {
        var cockpit = new CockpitViewModel();
        var actions = new PluginActions(cockpit, () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>(), Substitute.For<IDelegationService>());

        cockpit.SelectedSession = new SessionViewModel();
        actions.HasActiveSession.Should().BeTrue();

        cockpit.SelectedSession = null;
        actions.HasActiveSession.Should().BeFalse();
    }

    [Fact]
    public async Task InjectIntoActiveSessionAsync_AppendsToTheSdkSessionsInput()
    {
        var cockpit = new CockpitViewModel();
        var session = new SessionViewModel();
        cockpit.SelectedSession = session;
        var actions = new PluginActions(cockpit, () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>(), Substitute.For<IDelegationService>());

        await actions.InjectIntoActiveSessionAsync("issue #42: fix the thing");

        session.InputText.Should().Contain("issue #42: fix the thing");
    }

    [Fact]
    public async Task InjectIntoActiveSessionAsync_NoActiveSession_DoesNotThrow()
    {
        var cockpit = new CockpitViewModel { SelectedSession = null };
        var actions = new PluginActions(cockpit, () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>(), Substitute.For<IDelegationService>());

        var act = () => actions.InjectIntoActiveSessionAsync("x");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetClipboardTextAsync_WritesToTheClipboard()
    {
        // Avalonia 12's IClipboard.SetTextAsync is an extension over SetDataAsync(DataTransfer), so assert
        // a clipboard write happened rather than binding to the exact member the extension calls.
        var clipboard = Substitute.For<IClipboard>();
        var actions = new PluginActions(new CockpitViewModel(), () => clipboard, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>(), Substitute.For<IDelegationService>());

        await actions.SetClipboardTextAsync("copied");

        clipboard.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task SetClipboardTextAsync_NoClipboard_DoesNotThrow()
    {
        var actions = new PluginActions(new CockpitViewModel(), () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>(), Substitute.For<IDelegationService>());

        var act = () => actions.SetClipboardTextAsync("x");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartSessionAsync_ProfileThatDoesNotExist_SaysWhichOnesDo_RatherThanGuessing()
    {
        // Guessing between profiles would run someone's work on the wrong model, in the wrong directory, with the
        // wrong permissions — and the caller would never learn that it had guessed.
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync().Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
        [
            new SessionProfile("Work", "/home/raymond/.claude"),
            new SessionProfile("Private", "/home/raymond/.claude-private"),
        ]));

        var actions = new PluginActions(new CockpitViewModel(), () => null, Substitute.For<ISessionDialogService>(), profiles, Substitute.For<IDelegationService>());

        var act = () => actions.StartSessionAsync("Wrok", "do the thing");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Work, Private*");
    }

    [Fact]
    public async Task StartSessionAsync_WithNoProfilesAtAll_SaysSo()
    {
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync().Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([]));

        var actions = new PluginActions(new CockpitViewModel(), () => null, Substitute.For<ISessionDialogService>(), profiles, Substitute.For<IDelegationService>());

        var act = () => actions.StartSessionAsync("Work");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*No session profiles are configured*");
    }

    [Fact]
    public async Task DelegateAsync_HandsBackWhatTheProfileAnswered()
    {
        var delegation = _Delegation(_Task(DelegatedTaskStatus.Completed, result: "Done — 3 files changed"));

        var actions = new PluginActions(new CockpitViewModel(), () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>(), delegation);

        (await actions.DelegateAsync("reviewer", "review the diff")).Should().Be("Done — 3 files changed");
    }

    [Fact]
    public async Task DelegateAsync_WhenTheProfileFailed_Throws_RatherThanHandingBackNothing()
    {
        // A flow that took an empty string for an answer would carry on, and put "" wherever the answer was meant to
        // go — a comment on a ticket saying nothing at all, and a run that reports green.
        var delegation = _Delegation(_Task(DelegatedTaskStatus.Failed, error: "the model refused"));

        var actions = new PluginActions(new CockpitViewModel(), () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>(), delegation);

        var act = () => actions.DelegateAsync("reviewer", "review the diff");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*the model refused*");
    }

    [Fact]
    public async Task DelegateAsync_WhenItTakesTooLong_SaysTheTaskIsStillRunning()
    {
        // The task is not killed: it is real work, it is visible in the tasks view, and throwing it away because the
        // caller grew impatient would discard whatever it had already done.
        var delegation = _Delegation(_Task(DelegatedTaskStatus.Running));

        var actions = new PluginActions(new CockpitViewModel(), () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>(), delegation);

        var act = () => actions.DelegateAsync("reviewer", "review the diff", timeout: TimeSpan.Zero);

        (await act.Should().ThrowAsync<TimeoutException>()).WithMessage("*still running*");
    }

    private static IDelegationService _Delegation(DelegatedTaskView task)
    {
        var delegation = Substitute.For<IDelegationService>();
        delegation.DelegateAsync(Arg.Any<DelegationRequest>()).Returns(Task.FromResult(task));
        delegation.GetTask(task.TaskId).Returns(task);

        return delegation;
    }

    private static DelegatedTaskView _Task(DelegatedTaskStatus status, string? result = null, string? error = null) => new(
        "t1",
        "reviewer",
        Label: null,
        TaskType: null,
        status,
        DateTimeOffset.UtcNow,
        StartedAt: DateTimeOffset.UtcNow,
        FinishedAt: null,
        TurnCount: 1,
        result,
        error);
}
