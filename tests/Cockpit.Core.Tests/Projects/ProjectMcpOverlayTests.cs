using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// The variant-B merge (AC-159): the global registry is the base, and a project turns servers off, adds its own,
/// or overrides one by name. What a project's sessions actually get to talk to, so a wrong answer here is a
/// server a session silently does or does not have.
/// </summary>
public class ProjectMcpOverlayTests
{
    private static readonly IReadOnlyList<McpServerConfig> Registry =
    [
        new() { Name = "youtrack", Command = "npx" },
        new() { Name = "depot", Url = "https://depot.example/mcp" },
    ];

    /// <summary>A project saved by an earlier build still reads back the way it was written.</summary>
    [Fact]
    public void IsSelectedByDefault_TheOlderDisabledList_StillApplies()
    {
        var overlay = new ProjectMcpOverlay { DisabledServerNames = ["youtrack"] };

        Assert.False(overlay.IsSelectedByDefault("youtrack"));
        Assert.True(overlay.IsSelectedByDefault("depot"));
    }

}
