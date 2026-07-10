using Microsoft.Extensions.AI;
using Cockpit.Infrastructure.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="GatedTool"/>: an MCP tool runs only after the approval gate says yes — an approval invokes
/// the underlying tool, a denial returns a refusal without ever running it (#26 human-in-the-loop).
/// </summary>
public class GatedToolTests
{
    [Fact]
    public async Task Invoke_WhenApproved_RunsTheUnderlyingTool()
    {
        var calls = 0;
        AIFunction inner = AIFunctionFactory.Create(() => { calls++; return "the result"; }, "myTool");
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var tool = new GatedTool(inner, gate);

        var result = await tool.InvokeAsync();

        calls.Should().Be(1);
        result?.ToString().Should().Contain("the result");
    }

    [Fact]
    public async Task Invoke_WhenDenied_DoesNotRunTheTool_AndReturnsARefusal()
    {
        var calls = 0;
        AIFunction inner = AIFunctionFactory.Create(() => { calls++; return "the result"; }, "myTool");
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var tool = new GatedTool(inner, gate);

        var result = await tool.InvokeAsync();

        calls.Should().Be(0);
        result?.ToString().Should().Contain("denied");
    }

    [Fact]
    public async Task Invoke_WhenTheToolThrows_ReturnsTheErrorAsResult_WithoutRethrowing()
    {
        AIFunction inner = AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException("bad path")), "myTool");
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var tool = new GatedTool(inner, gate);

        // A tool error must not abort the turn: it comes back as the result (so the model can react) and is
        // reported as an error, not thrown.
        var result = await tool.InvokeAsync();

        result?.ToString().Should().Contain("bad path");
        gate.Received().ReportToolResult(Arg.Any<string>(), Arg.Is<string>(s => s.Contains("bad path")), true);
    }
}
