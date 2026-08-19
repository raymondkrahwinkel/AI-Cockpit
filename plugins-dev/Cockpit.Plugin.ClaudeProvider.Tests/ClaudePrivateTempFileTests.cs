namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudePrivateTempFile.SweepStale` (AC-956) — the backstop for the files a killed session never got to delete.
// Both routes already delete on teardown, but that delete is bounded by the app's exit budget; a sweep at plugin
// start is what does not depend on a deadline. What must hold is the pair: what is left over goes, what a live
// session is still using stays.
public class ClaudePrivateTempFileTests
{
    [Fact]
    public void SweepStale_RemovesWhatIsLeftBehind_AndKeepsWhatIsInUse()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cockpit-sweep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var leftBehind = Path.Combine(directory, "left-behind.json");
            var inUse = Path.Combine(directory, "in-use.json");
            File.WriteAllText(leftBehind, "{}");
            File.WriteAllText(inUse, "{}");
            File.SetLastWriteTimeUtc(leftBehind, DateTime.UtcNow - TimeSpan.FromDays(2));

            ClaudePrivateTempFile.SweepStale(directory, TimeSpan.FromDays(1));

            Assert.False(File.Exists(leftBehind));
            Assert.True(File.Exists(inUse));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Housekeeping never fails a launch: `Initialize` calls this before anything else the plugin registers, so a
    // temp directory that is missing (a fresh machine) or unreadable must be a no-op rather than a throw.
    [Fact]
    public void SweepStale_IsANoOp_WhenTheDirectoryIsNotThere()
    {
        ClaudePrivateTempFile.SweepStale(
            Path.Combine(Path.GetTempPath(), $"cockpit-sweep-missing-{Guid.NewGuid():N}"), TimeSpan.FromDays(1));
    }

    // The written file is what the CLI is handed, so it has to hold the content byte for byte — a prompt that is
    // truncated or re-encoded on the way to disk would be a silent change to what a session was told.
    [Fact]
    public void WriteSystemPrompt_WritesTheWholePrompt_AndSkipsAnEmptyOne()
    {
        Assert.Null(ClaudePrivateTempFile.WriteSystemPrompt(null));
        Assert.Null(ClaudePrivateTempFile.WriteSystemPrompt("   "));

        var prompt = new string('x', 40_000);
        var path = ClaudePrivateTempFile.WriteSystemPrompt(prompt)!;
        try
        {
            Assert.Equal(prompt, File.ReadAllText(path));
        }
        finally
        {
            ClaudePrivateTempFile.Delete(path);
        }

        Assert.False(File.Exists(path));
    }
}
