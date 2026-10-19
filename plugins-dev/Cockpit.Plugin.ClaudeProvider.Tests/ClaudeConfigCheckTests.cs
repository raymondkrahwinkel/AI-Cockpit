namespace Cockpit.Plugin.ClaudeProvider.Tests;

public class ClaudeConfigCheckTests
{
    [Fact]
    public void Check_UsesTheGivenExistenceChecks_NotTheLocalDisk()
    {
        var missing = ClaudeConfigCheck.Check(new ClaudeCheckConfigRequest("/srv/nope", null), _ => null, _ => false, _ => false);
        Assert.False(missing.ConfigDirOk);
        Assert.False(missing.ExecutableOk);

        var pinned = ClaudeConfigCheck.Check(new ClaudeCheckConfigRequest("", "/opt/claude"), _ => null, _ => false, _ => true);
        Assert.True(pinned.ConfigDirOk);
        Assert.Null(pinned.ConfigDirText);
        Assert.StartsWith("Using pinned path", pinned.ExecutableText);
    }
}
