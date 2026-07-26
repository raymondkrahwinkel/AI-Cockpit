using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A flow naming the session it is running on (<c>cockpit.set-status</c> → <see cref="PluginActions"/>) is the fourth
/// way a session gets a name somebody meant, next to the New-session dialog, an inline rename and
/// <c>SetSessionName</c> — so a ticket linked to that session afterwards must offer its name rather than take it
/// (#AC-310). Here rather than in the unit tests because the call marshals to the UI thread: without a pumping
/// dispatcher, awaiting it never returns. The session is built on that thread and the call awaited off it, which is
/// the one arrangement where the marshalling actually completes.
/// </summary>
[Collection("avalonia")]
public class PluginActionsSessionNameTests
{
    [Fact]
    public async Task SetActiveSessionStatusAsync_WithAName_ClaimsIt()
    {
        var (actions, session) = _ActionsOnAFreshSession();

        await actions.SetActiveSessionStatusAsync("running the suite", "release work");

        Dispatcher.UIThread.Invoke(() =>
        {
            session.Title.Should().Be("release work");
            session.Statusline.Should().Be("running the suite");
            session.SuggestName("AC-310").Should().BeFalse();
            session.Title.Should().Be("release work");
        });
    }

    [Fact]
    public async Task SetActiveSessionStatusAsync_WithoutAName_LeavesTheSessionOpenToBeingLabelled()
    {
        var (actions, session) = _ActionsOnAFreshSession();

        // A status without a name says what the session is doing, not what it is called — it claims nothing.
        await actions.SetActiveSessionStatusAsync("running the suite");

        Dispatcher.UIThread.Invoke(() =>
        {
            session.SuggestName("AC-310").Should().BeTrue();
            session.Title.Should().Be("AC-310");
        });
    }

    private static (PluginActions Actions, SessionViewModel Session) _ActionsOnAFreshSession() =>
        Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var session = new SessionViewModel();
            cockpit.SelectedSession = session;
            var actions = new PluginActions(
                cockpit,
                () => null,
                Substitute.For<ISessionDialogService>(),
                Substitute.For<ISessionProfileStore>(),
                Substitute.For<IDelegationService>());

            return (actions, session);
        });
}
