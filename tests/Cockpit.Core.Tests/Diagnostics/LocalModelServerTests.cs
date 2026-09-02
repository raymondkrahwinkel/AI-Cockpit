using Cockpit.Core.Diagnostics;

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
        // like a session is — and as one line, because the runner counted on its own as well would put the model in
        // the panel twice and claim more memory in use than the machine has.
        var rows = new List<ProcessRow>
        {
            new(1, 0, TimeSpan.Zero, 0, "systemd"),
            new(100, 1, TimeSpan.FromSeconds(4), 40_000_000, "ollama"),
            new(101, 100, TimeSpan.FromSeconds(30), 5_000_000_000, "ollama runner"),
        };

        var servers = LocalModelServers.From(rows);

        Assert.Single(servers);
        Assert.Equal("Ollama", servers[0].Name);
        Assert.Equal(5_040_000_000, servers[0].MemoryBytes);
    }

    [Fact]
    public void AServerRunningWithoutAModel_StillShows()
    {
        // What tells the operator the memory went with the model rather than with the server.
        var rows = new List<ProcessRow> { new(100, 1, TimeSpan.Zero, 40_000_000, "ollama") };

        Assert.Equal(40_000_000, LocalModelServers.From(rows).Single().MemoryBytes);
    }

    [Fact]
    public void TwoDifferentServers_AreReportedApart_HeaviestFirst()
    {
        var rows = new List<ProcessRow>
        {
            new(100, 1, TimeSpan.Zero, 40_000_000, "ollama"),
            new(200, 1, TimeSpan.Zero, 900_000_000, "LM Studio"),
        };

        Assert.Equal(new[] { "LM Studio", "Ollama" }, LocalModelServers.From(rows).Select(server => server.Name));
    }

    [Fact]
    public void AMachineWithNoModelServer_ReportsNone()
    {
        var rows = new List<ProcessRow>
        {
            new(1, 0, TimeSpan.Zero, 0, "systemd"),
            new(50, 1, TimeSpan.Zero, 700_000_000, "claude"),
        };

        Assert.Empty(LocalModelServers.From(rows));
    }
}
