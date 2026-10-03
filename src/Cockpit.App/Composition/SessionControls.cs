using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.Composition;

// AC-1449: the session control a pane gets outside the container, and the seam that drives one without a runtime.
internal static class SessionControls
{
    // The previewer, the renders and the unit-test graph: a control that cannot launch.
    public static ISessionControlFactory DesignTime { get; } = new SessionControlFactory(manager: null);

    // The host's own pump step, without the UI dispatcher between; every pane outside a remote one has a host.
    internal static void Apply(this SessionViewModel pane, SessionEvent evt) => ((SessionHost)pane.Control).Pump(evt);
}
