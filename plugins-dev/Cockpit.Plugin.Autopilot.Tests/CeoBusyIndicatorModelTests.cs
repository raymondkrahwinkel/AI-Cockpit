using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The CEO-only "working" cue state (AC-195): <see cref="CeoBusyIndicatorModel"/> mirrors the embedded session's
/// busy signal so the "Plan with the CEO" pop-out can show the CEO is still planning during a long silent turn. Only
/// the state rule is tested here — the actual pill rendering is a view concern and is not unit-testable.
/// </summary>
public class CeoBusyIndicatorModelTests
{
    [Fact]
    public void SeedsFromTheSession_AndPushesTheInitialStateOnce()
    {
        var session = new FakeCeoSession { IsBusy = true };
        var pushed = new List<bool>();

        using var model = new CeoBusyIndicatorModel(session, pushed.Add);

        model.IsWorking.Should().BeTrue();
        pushed.Should().Equal(true);
    }

    [Fact]
    public void FollowsBusyChanged_LightingAndClearingTheCue()
    {
        var session = new FakeCeoSession();
        var pushed = new List<bool>();

        using var model = new CeoBusyIndicatorModel(session, pushed.Add);

        session.Raise(true);
        model.IsWorking.Should().BeTrue();

        session.Raise(false);
        model.IsWorking.Should().BeFalse();

        // The initial push plus each flip, in order.
        pushed.Should().Equal(false, true, false);
    }

    [Fact]
    public void Dispose_StopsFollowingTheSignal()
    {
        var session = new FakeCeoSession();
        var pushed = new List<bool>();
        var model = new CeoBusyIndicatorModel(session, pushed.Add);

        model.Dispose();
        session.Raise(true);

        model.IsWorking.Should().BeFalse();
        pushed.Should().Equal(false);
    }

    private sealed class FakeCeoSession : IEmbeddedSession
    {
        // Never read by the indicator model; kept off the platform so the test needs no Avalonia runtime.
        public Control View => null!;

        public string PaneId => "ceo";

        public Task<string?> Completion { get; } = new TaskCompletionSource<string?>().Task;

        public bool IsBusy { get; set; }

        public event Action<bool>? BusyChanged;

        public void SetInputEnabled(bool enabled)
        {
        }

        public Task CloseAsync() => Task.CompletedTask;

        public void Raise(bool busy)
        {
            IsBusy = busy;
            BusyChanged?.Invoke(busy);
        }
    }
}
