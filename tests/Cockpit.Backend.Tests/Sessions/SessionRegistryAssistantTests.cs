using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Sessions;

/// <summary>
/// AC-1374: the assistant is tracked apart from the grid/embedded pool — reachable through
/// <see cref="SessionRegistry.Assistant"/>, but never through <see cref="SessionRegistry.All"/>, so every existing
/// reader of <c>All</c> (SessionWorkspaces, PaneWorkspaceDirectory) keeps seeing exactly what it sees today.
/// </summary>
public class SessionRegistryAssistantTests
{
    [Fact]
    public void TheAssistant_IsReachableThroughItsOwnSlot_NeverThroughAll_AndClearsOnUnregister()
    {
        var registry = new SessionRegistry();
        var grid = Substitute.For<ISessionHandle>();
        grid.PaneId.Returns("grid-pane");
        registry.Register(grid);

        var assistant = Substitute.For<ISessionHandle>();
        assistant.PaneId.Returns("assistant-pane");
        registry.RegisterAssistant(assistant);

        Assert.Equal([grid], registry.All);
        Assert.Same(assistant, registry.Assistant);

        registry.UnregisterAssistant();

        Assert.Null(registry.Assistant);
        Assert.Equal([grid], registry.All);
    }
}
