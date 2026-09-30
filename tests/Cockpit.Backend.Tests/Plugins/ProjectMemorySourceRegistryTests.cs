using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Backend.Tests.Plugins;

/// <summary>
/// Which memory sources the project editor's picker and a starting session's standing instructions both end up
/// reading (AC-165/166). Case-insensitive on purpose, unlike <see cref="ProjectFieldRegistryTests"/>'s key
/// comparison: a project's own <c>MemoryRef</c> is matched the same way when it is read back
/// (<see cref="Cockpit.Core.Sessions.SessionStartDefaults"/>), so the registry has to agree with that rule rather
/// than the field registry's.
/// </summary>
public class ProjectMemorySourceRegistryTests
{
    private static ProjectMemorySourceRegistration Source(string scheme, string title, string instruction = "Read it there.") =>
        new(scheme, title, instruction);

    [Fact]
    public void Remove_ThenRegisterTheSameSchemeAgain_Succeeds()
    {
        // AC-501's live-refresh path (a connection renamed, its old scheme reclaimed and the new content
        // re-registered) leans on this: a scheme that was just freed must be immediately re-registrable, not stuck
        // refused the way a still-taken scheme is.
        var registry = new ProjectMemorySourceRegistry();
        registry.Register(Source("depot", "Depot project"));
        registry.Remove("depot");

        Assert.True(registry.Register(Source("depot", "Depot project (renamed)")));

        Assert.Equal("Depot project (renamed)", Assert.Single(registry.Sources).Title);
    }

    // --- AC-499: RegisterFamily/Families --------------------------------------------------------------------------

}
