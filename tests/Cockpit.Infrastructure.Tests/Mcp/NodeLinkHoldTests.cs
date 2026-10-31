using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

public sealed class NodeLinkHoldTests
{
    [Fact]
    public void PauseSurvivesARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"paused-nodes-{Guid.NewGuid():N}.txt");
        try
        {
            var gate = new NodeLinkGate(path);
            gate.SetPaused("jsll", true);
            Assert.True(new NodeLinkGate(path).IsPaused("jsll"));
            Assert.Null(new NodeLinkGate(path).RefusalFor("other"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
