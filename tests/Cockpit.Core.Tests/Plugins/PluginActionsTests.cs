using Avalonia.Input.Platform;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The cockpit actions a plugin can perform (#14): inject into the active session, put text on the clipboard.</summary>
public class PluginActionsTests
{
    [Fact]
    public void HasActiveSession_ReflectsTheSelectedSession()
    {
        var cockpit = new CockpitViewModel();
        var actions = new PluginActions(cockpit, () => null);

        cockpit.SelectedSession = new ClaudeSessionViewModel();
        actions.HasActiveSession.Should().BeTrue();

        cockpit.SelectedSession = null;
        actions.HasActiveSession.Should().BeFalse();
    }

    [Fact]
    public async Task InjectIntoActiveSessionAsync_AppendsToTheSdkSessionsInput()
    {
        var cockpit = new CockpitViewModel();
        var session = new ClaudeSessionViewModel();
        cockpit.SelectedSession = session;
        var actions = new PluginActions(cockpit, () => null);

        await actions.InjectIntoActiveSessionAsync("issue #42: fix the thing");

        session.InputText.Should().Contain("issue #42: fix the thing");
    }

    [Fact]
    public async Task InjectIntoActiveSessionAsync_NoActiveSession_DoesNotThrow()
    {
        var cockpit = new CockpitViewModel { SelectedSession = null };
        var actions = new PluginActions(cockpit, () => null);

        var act = () => actions.InjectIntoActiveSessionAsync("x");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetClipboardTextAsync_WritesToTheClipboard()
    {
        // Avalonia 12's IClipboard.SetTextAsync is an extension over SetDataAsync(DataTransfer), so assert
        // a clipboard write happened rather than binding to the exact member the extension calls.
        var clipboard = Substitute.For<IClipboard>();
        var actions = new PluginActions(new CockpitViewModel(), () => clipboard);

        await actions.SetClipboardTextAsync("copied");

        clipboard.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task SetClipboardTextAsync_NoClipboard_DoesNotThrow()
    {
        var actions = new PluginActions(new CockpitViewModel(), () => null);

        var act = () => actions.SetClipboardTextAsync("x");

        await act.Should().NotThrowAsync();
    }
}
