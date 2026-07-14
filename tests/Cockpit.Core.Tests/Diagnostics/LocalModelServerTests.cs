using Cockpit.Core.Diagnostics;
using FluentAssertions;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Finding the local model servers on the machine (#78). A session that talks to Ollama over HTTP spawns nothing of
/// its own, so the breakdown had nothing to say about the heaviest thing running — the model. It is not the cockpit's
/// child, so it is found by name and reported apart from the cockpit's total.
/// </summary>
public class LocalModelServerTests
{
    [Fact]
    public void ARunningOllama_IsFoundWithTheModelItLoaded()
    {
        // Ollama keeps the model in a child process, which is where the gigabytes are. Measured as a tree, exactly
        // like a session is.
        var rows = new List<ProcessRow>
        {
            new(1, 0, TimeSpan.Zero, 0, "systemd"),
            new(100, 1, TimeSpan.FromSeconds(4), 40_000_000, "ollama"),
            new(101, 100, TimeSpan.FromSeconds(30), 5_000_000_000, "ollama runner"),
        };

        var servers = LocalModelServers.From(rows);

        servers.Should().ContainSingle();
        servers[0].Name.Should().Be("Ollama");
        servers[0].MemoryBytes.Should().Be(5_040_000_000);
    }

    // The runner is inside the server's tree. Counted on its own as well, the model would appear twice — and the panel
    // would claim more memory in use than the machine has.
    [Fact]
    public void TheModelRunner_IsNotCountedTwice()
    {
        var rows = new List<ProcessRow>
        {
            new(100, 1, TimeSpan.Zero, 40_000_000, "ollama"),
            new(101, 100, TimeSpan.Zero, 5_000_000_000, "ollama runner"),
        };

        LocalModelServers.From(rows).Single().MemoryBytes.Should().Be(5_040_000_000);
    }

    [Fact]
    public void AServerRunningWithoutAModel_StillShows()
    {
        // What tells the operator the memory went with the model rather than with the server.
        var rows = new List<ProcessRow> { new(100, 1, TimeSpan.Zero, 40_000_000, "ollama") };

        LocalModelServers.From(rows).Single().MemoryBytes.Should().Be(40_000_000);
    }

    [Fact]
    public void TwoDifferentServers_AreReportedApart_HeaviestFirst()
    {
        var rows = new List<ProcessRow>
        {
            new(100, 1, TimeSpan.Zero, 40_000_000, "ollama"),
            new(200, 1, TimeSpan.Zero, 900_000_000, "LM Studio"),
        };

        LocalModelServers.From(rows).Select(server => server.Name).Should().Equal("LM Studio", "Ollama");
    }

    [Fact]
    public void AMachineWithNoModelServer_ReportsNone()
    {
        var rows = new List<ProcessRow>
        {
            new(1, 0, TimeSpan.Zero, 0, "systemd"),
            new(50, 1, TimeSpan.Zero, 700_000_000, "claude"),
        };

        LocalModelServers.From(rows).Should().BeEmpty();
    }
}
