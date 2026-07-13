using Avalonia.Input.Platform;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using FluentAssertions;
using NSubstitute;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The cockpit actions a plugin can perform (#14): inject into the active session, put text on the clipboard.</summary>
public class PluginActionsTests
{
    [Fact]
    public void HasActiveSession_ReflectsTheSelectedSession()
    {
        var cockpit = new CockpitViewModel();
        var actions = new PluginActions(cockpit, () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>());

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
        var actions = new PluginActions(cockpit, () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>());

        await actions.InjectIntoActiveSessionAsync("issue #42: fix the thing");

        session.InputText.Should().Contain("issue #42: fix the thing");
    }

    [Fact]
    public async Task InjectIntoActiveSessionAsync_NoActiveSession_DoesNotThrow()
    {
        var cockpit = new CockpitViewModel { SelectedSession = null };
        var actions = new PluginActions(cockpit, () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>());

        var act = () => actions.InjectIntoActiveSessionAsync("x");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetClipboardTextAsync_WritesToTheClipboard()
    {
        // Avalonia 12's IClipboard.SetTextAsync is an extension over SetDataAsync(DataTransfer), so assert
        // a clipboard write happened rather than binding to the exact member the extension calls.
        var clipboard = Substitute.For<IClipboard>();
        var actions = new PluginActions(new CockpitViewModel(), () => clipboard, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>());

        await actions.SetClipboardTextAsync("copied");

        clipboard.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task SetClipboardTextAsync_NoClipboard_DoesNotThrow()
    {
        var actions = new PluginActions(new CockpitViewModel(), () => null, Substitute.For<ISessionDialogService>(), Substitute.For<ISessionProfileStore>());

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

        var actions = new PluginActions(new CockpitViewModel(), () => null, Substitute.For<ISessionDialogService>(), profiles);

        var act = () => actions.StartSessionAsync("Wrok", "do the thing");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Work, Private*");
    }

    [Fact]
    public async Task StartSessionAsync_WithNoProfilesAtAll_SaysSo()
    {
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync().Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([]));

        var actions = new PluginActions(new CockpitViewModel(), () => null, Substitute.For<ISessionDialogService>(), profiles);

        var act = () => actions.StartSessionAsync("Work");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*No session profiles are configured*");
    }
}
