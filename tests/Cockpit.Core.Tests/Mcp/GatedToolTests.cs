using Microsoft.Extensions.AI;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="GatedTool"/>: an MCP tool runs only after the approval gate says yes — an approval invokes
/// the underlying tool, a denial returns a refusal without ever running it (#26 human-in-the-loop).
/// </summary>
public class GatedToolTests
{
    [Fact]
    public async Task Invoke_WhenDenied_DoesNotRunTheTool_AndReturnsARefusal()
    {
        var calls = 0;
        AIFunction inner = AIFunctionFactory.Create(() => { calls++; return "the result"; }, "myTool");
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ToolApprovalResult.Deny(null));
        var tool = new GatedTool(inner, gate);

        var result = await tool.InvokeAsync();

        Assert.Equal(0, calls);
        Assert.Contains("denied", result?.ToString());
    }
}
