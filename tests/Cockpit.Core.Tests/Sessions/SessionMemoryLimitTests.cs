using Cockpit.Core.Sessions;
using FluentAssertions;

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
        SessionMemoryLimit.NodeOptions(existing: null, megabytes: null).Should().BeNull();

    [Fact]
    public void ALimit_BecomesTheNodeFlag() =>
        SessionMemoryLimit.NodeOptions(null, 1024).Should().Be("--max-old-space-size=1024");

    [Fact]
    public void AnExistingNodeOptions_IsKept_AndAppendedTo() =>
        SessionMemoryLimit.NodeOptions("--enable-source-maps", 1024)
            .Should().Be("--enable-source-maps --max-old-space-size=1024");

    [Fact]
    public void ACapTheOperatorSetThemselves_Wins_BecauseSilentlyOverridingItWouldBeUndebuggable() =>
        SessionMemoryLimit.NodeOptions("--max-old-space-size=4096", 512)
            .Should().Be("--max-old-space-size=4096");

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(255)]
    public void ACeilingTooLowToStartAConversation_IsIgnored_RatherThanGuaranteeingACrash(int megabytes) =>
        SessionMemoryLimit.NodeOptions(null, megabytes).Should().BeNull();
}
