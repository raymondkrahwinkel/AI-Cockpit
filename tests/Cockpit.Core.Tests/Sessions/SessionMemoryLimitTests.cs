using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The memory ceiling on a session's CLI. Every rule here is about not surprising someone: it is off unless asked
/// for, it never overrides a number the operator put in their own environment, and it refuses a value so small that
/// the session could not start at all — a "limit" that guarantees a crash is not a limit, it is a trap.
/// </summary>
public class SessionMemoryLimitTests
{
    [Fact]
    public void NoProfileLimit_LeavesTheEnvironmentAlone() =>
        Assert.Null(SessionMemoryLimit.NodeOptions(existing: null, megabytes: null));

    [Fact]
    public void ALimit_BecomesTheNodeFlag() =>
        Assert.Equal("--max-old-space-size=1024", SessionMemoryLimit.NodeOptions(null, 1024));

    [Fact]
    public void AnExistingNodeOptions_IsKept_AndAppendedTo() =>
        Assert.Equal(
            "--enable-source-maps --max-old-space-size=1024",
            SessionMemoryLimit.NodeOptions("--enable-source-maps", 1024));

    [Fact]
    public void ACapTheOperatorSetThemselves_Wins_BecauseSilentlyOverridingItWouldBeUndebuggable() =>
        Assert.Equal("--max-old-space-size=4096", SessionMemoryLimit.NodeOptions("--max-old-space-size=4096", 512));

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(255)]
    public void ACeilingTooLowToStartAConversation_IsIgnored_RatherThanGuaranteeingACrash(int megabytes) =>
        Assert.Null(SessionMemoryLimit.NodeOptions(null, megabytes));
}
