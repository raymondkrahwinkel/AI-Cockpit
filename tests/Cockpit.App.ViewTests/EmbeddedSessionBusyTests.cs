using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The host adapter's turn-busy forwarding (AC-195): <see cref="EmbeddedSession"/> mirrors the embedded
/// <see cref="SessionViewModel.IsBusy"/> and raises <c>BusyChanged</c> on each flip, so an embedder — the Autopilot
/// plan pop-out's CEO cue — can show the session is mid-turn without touching the shared global session indicator.
/// </summary>
[Collection("avalonia")]
public class EmbeddedSessionBusyTests
{
    [Fact]
    public void IsBusy_MirrorsTheSession_AndBusyChangedFiresOnlyOnEachFlip() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        var completion = new TaskCompletionSource<string?>();
        var embedded = new EmbeddedSession(new ContentControl(), session, completion.Task, _ => { }, () => Task.CompletedTask);

        var events = new List<bool>();
        embedded.BusyChanged += events.Add;

        embedded.IsBusy.Should().BeFalse();

        session.IsBusy = true;
        embedded.IsBusy.Should().BeTrue();

        // A touch that does not change the value must not fan out a redundant event.
        session.IsBusy = true;

        session.IsBusy = false;
        embedded.IsBusy.Should().BeFalse();

        events.Should().Equal(true, false);
    });
}
