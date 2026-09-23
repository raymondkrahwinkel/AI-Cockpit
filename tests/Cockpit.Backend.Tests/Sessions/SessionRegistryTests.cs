using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Sessions;

/// <summary>
/// AC-1373: the registry is one entry per pane id, and says so each time that set changes, which is what a
/// consumer that caches a roster (F1.2 onward) will listen for.
/// </summary>
public class SessionRegistryTests
{
    [Fact]
    public void RegisteringAndRemoving_KeepsOneEntryPerPane_AndRaisesChangedOnlyWhenTheSetChanged()
    {
        var registry = new SessionRegistry();
        var changes = 0;
        registry.Changed += (_, _) => changes++;
        var first = _Handle("pane-a");
        var other = _Handle("pane-b");
        var replacement = _Handle("pane-a");

        registry.Register(first);
        registry.Register(other);
        registry.Register(replacement);
        registry.Unregister("pane-b");
        registry.Unregister("pane-nobody-registered");

        Assert.Equal([replacement], registry.All);
        Assert.Same(replacement, registry.Find("pane-a"));
        Assert.Null(registry.Find("pane-b"));
        Assert.Equal(4, changes);
    }

    private static ISessionHandle _Handle(string paneId)
    {
        var handle = Substitute.For<ISessionHandle>();
        handle.PaneId.Returns(paneId);
        return handle;
    }
}
