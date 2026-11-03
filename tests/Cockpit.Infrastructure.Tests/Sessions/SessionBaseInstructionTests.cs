using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

public class SessionBaseInstructionTests
{
    // AC-1942 (L2 data loss): a scratch directory without a marker is set aside intact, not a permanent block.
    [Fact]
    public void Create_DirectoryWithoutMarker_MovesItAsideIntactAndStartsAFreshOwnedOne()
    {
        var previous = Environment.GetEnvironmentVariable(SessionScratch.BaseVariable);
        var basis = Path.Combine(Path.GetTempPath(), $"cockpit-scratch-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(SessionScratch.BaseVariable, basis);
            var pane = Guid.NewGuid().ToString("N");
            var path = SessionScratch.PathFor(pane);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "render.png"), "keep me");

            var created = SessionScratch.Create(pane);

            Assert.Equal(path, created);
            Assert.Equal(pane, SessionScratch.OwnerOf(created));
            var orphan = Assert.Single(Directory.GetDirectories(basis, Path.GetFileName(path) + "-orphan-*"));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(orphan, "render.png")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SessionScratch.BaseVariable, previous);
            Directory.Delete(basis, recursive: true);
        }
    }
}
