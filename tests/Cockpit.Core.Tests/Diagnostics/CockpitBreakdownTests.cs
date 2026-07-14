using Cockpit.Core.Diagnostics;
using FluentAssertions;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Making the total explicable (#78). Opening one Ollama session took the figure from 300 MB to 800 with nothing on
/// screen to account for it: the session runs over HTTP and has no process, but the MCP tool servers it connected to
/// are the cockpit's own children and are counted in its tree. They are the missing 500 MB, and they had no line.
/// </summary>
public class CockpitBreakdownTests
{
    [Fact]
    public void TheToolServersTheCockpitStarted_AreEachAccountedFor()
    {
        var rows = new List<ProcessRow>
        {
            new(10, 1, TimeSpan.Zero, 300_000_000, "Cockpit.App"),
            new(20, 10, TimeSpan.Zero, 95_000_000, "npm exec @model"),
            new(30, 10, TimeSpan.Zero, 45_000_000, "uv"),
        };

        var parts = CockpitBreakdown.From(rows, cockpitProcessId: 10, sessionProcessIds: []);

        parts.OwnBytes.Should().Be(300_000_000);
        parts.Children.Select(child => child.Name).Should().Equal("npm exec @model", "uv");
        parts.Children.Sum(child => child.MemoryBytes).Should().Be(140_000_000);
    }

    // An "npm exec" is a shell around the node process doing the work, and the memory is in the child.
    [Fact]
    public void AToolServer_IsMeasuredAsAWholeTree()
    {
        var rows = new List<ProcessRow>
        {
            new(10, 1, TimeSpan.Zero, 300_000_000, "Cockpit.App"),
            new(20, 10, TimeSpan.Zero, 5_000_000, "npm exec @model"),
            new(21, 20, TimeSpan.Zero, 90_000_000, "node"),
        };

        CockpitBreakdown.From(rows, 10, []).Children.Single().MemoryBytes.Should().Be(95_000_000);
    }

    // A session already has a section of its own; counted here too, the parts would add up to more than the whole.
    [Fact]
    public void ASessionsOwnProcess_IsNotCountedAmongTheToolServers()
    {
        var rows = new List<ProcessRow>
        {
            new(10, 1, TimeSpan.Zero, 300_000_000, "Cockpit.App"),
            new(40, 10, TimeSpan.Zero, 700_000_000, "claude"),
            new(20, 10, TimeSpan.Zero, 95_000_000, "npm exec @model"),
        };

        var parts = CockpitBreakdown.From(rows, 10, [40]);

        parts.Children.Select(child => child.Name).Should().Equal("npm exec @model");
    }

    // Two servers started the same way are one line: "npm exec" twice over is not something the operator can tell
    // apart, let alone act on separately.
    [Fact]
    public void TwoToolServersWithTheSameName_AreOneLine_AddedUp()
    {
        var rows = new List<ProcessRow>
        {
            new(10, 1, TimeSpan.Zero, 300_000_000, "Cockpit.App"),
            new(20, 10, TimeSpan.Zero, 95_000_000, "npm exec @model"),
            new(21, 10, TimeSpan.Zero, 93_000_000, "npm exec @model"),
        };

        var child = CockpitBreakdown.From(rows, 10, []).Children.Single();

        child.Name.Should().Be("npm exec @model ×2");
        child.MemoryBytes.Should().Be(188_000_000);
    }

    [Fact]
    public void TheParts_AddUpToTheTreeTheStatusBarShows()
    {
        var rows = new List<ProcessRow>
        {
            new(10, 1, TimeSpan.Zero, 300_000_000, "Cockpit.App"),
            new(20, 10, TimeSpan.Zero, 95_000_000, "npm exec @model"),
            new(30, 10, TimeSpan.Zero, 45_000_000, "uv"),
            new(40, 10, TimeSpan.Zero, 700_000_000, "claude"),
        };

        var total = ProcessTree.Sum(rows, 10).WorkingSetBytes;
        var parts = CockpitBreakdown.From(rows, 10, [40]);
        var session = ProcessTree.Sum(rows, 40).WorkingSetBytes;

        // The app, its tool servers and its sessions are the whole of what the status bar reports — which is what
        // makes a number that jumped by 500 MB something the operator can explain.
        (parts.OwnBytes + parts.Children.Sum(child => child.MemoryBytes) + session).Should().Be(total);
    }
}
