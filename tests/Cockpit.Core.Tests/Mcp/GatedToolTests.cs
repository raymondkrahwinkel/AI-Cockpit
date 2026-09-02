using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;
using Cockpit.Infrastructure.Mcp;
using ModelContextProtocol.Client;
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
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ToolApprovalResult.Allow);
        var tool = new GatedTool(inner, gate);

        var result = await tool.InvokeAsync();

        Assert.Equal(1, calls);
        Assert.Equal("the result", result?.ToString());
        gate.Received().ReportToolResult(Arg.Any<string>(), "the result", false);
    }

    [Fact]
    public async Task Invoke_WhenAnActualMcpResultIsNotTruncated_PreservesItsType()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await InProcessMcpHttpServer.StartAsync<McpTestToolA>();
        await using var client = await McpClientConnector.ConnectAsync(_TransportTo(server), null, timeout.Token);
        var actualTool = Assert.Single(await client.ListToolsAsync(cancellationToken: timeout.Token));
        var actualResult = await actualTool.InvokeAsync(cancellationToken: timeout.Token);
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), actualTool.Name, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ToolApprovalResult.Allow);
        var tool = new GatedTool(actualTool, gate);

        var result = await tool.InvokeAsync(cancellationToken: timeout.Token);

        Assert.Equal(actualResult?.GetType(), result?.GetType());
    }

    [Fact]
    public async Task Invoke_WhenResultIsTooLarge_ReturnsAndReportsAnExplicitLineSafeWarning()
    {
        var largeResult = string.Join('\n', Enumerable.Range(1, 20_000).Select(index => $"line-{index:D6}"));
        AIFunction inner = AIFunctionFactory.Create(() => largeResult, "myTool");
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ToolApprovalResult.Allow);
        var tool = new GatedTool(inner, gate);

        var result = (await tool.InvokeAsync())?.ToString();

        Assert.StartsWith("Tool result truncated by Cockpit.", result);
        Assert.Contains("Refine or paginate the tool call", result);
        Assert.Contains("line-000001", result);
        Assert.DoesNotContain("line-020000", result);
        Assert.True(result!.Length <= 131_072);
        var counts = Regex.Match(result, @"Original result: (?<totalChars>\d+) chars across (?<totalLines>\d+) lines\.\nShown: first (?<shownChars>\d+) chars across (?<shownLines>\d+) complete lines\.\nOmitted: (?<omittedChars>\d+) chars and (?<omittedLines>\d+) lines\.");
        Assert.True(counts.Success);
        Assert.Equal(largeResult.Length, int.Parse(counts.Groups["totalChars"].Value));
        Assert.Equal(20_000, int.Parse(counts.Groups["totalLines"].Value));
        Assert.Equal(int.Parse(counts.Groups["totalChars"].Value), int.Parse(counts.Groups["shownChars"].Value) + int.Parse(counts.Groups["omittedChars"].Value));
        Assert.Equal(int.Parse(counts.Groups["totalLines"].Value), int.Parse(counts.Groups["shownLines"].Value) + int.Parse(counts.Groups["omittedLines"].Value));
        gate.Received().ReportToolResult(Arg.Any<string>(), result!, false);
    }

    [Fact]
    public async Task Invoke_WhenResultIsOneLongJsonLine_DoesNotReturnAPartialPreview()
    {
        var largeResult = "{\"items\":\"" + new string('x', 200_000) + "\"}";
        AIFunction inner = AIFunctionFactory.Create(() => largeResult, "myTool");
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ToolApprovalResult.Allow);
        var tool = new GatedTool(inner, gate);

        var result = (await tool.InvokeAsync())?.ToString();

        Assert.Contains("--- Preview ---\n", result);
        Assert.DoesNotContain("{\"items\"", result);
    }

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

    [Fact]
    public async Task Invoke_WhenDeniedWithALargeReason_ReturnsAndReportsAnExplicitWarning()
    {
        var largeReason = string.Join('\n', Enumerable.Range(1, 20_000).Select(index => $"reason-{index:D6}"));
        AIFunction inner = AIFunctionFactory.Create(() => "the result", "myTool");
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ToolApprovalResult.Deny(largeReason));
        var tool = new GatedTool(inner, gate);

        var result = (await tool.InvokeAsync())?.ToString();

        Assert.StartsWith("Tool result truncated by Cockpit.", result);
        Assert.Contains("Refine or paginate the tool call", result);
        gate.Received().ReportToolResult(Arg.Any<string>(), result!, true);
    }

    [Fact]
    public async Task Invoke_WhenTheToolThrows_ReturnsTheErrorAsResult_WithoutRethrowing()
    {
        AIFunction inner = AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException("bad path")), "myTool");
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ToolApprovalResult.Allow);
        var tool = new GatedTool(inner, gate);

        // A tool error must not abort the turn: it comes back as the result (so the model can react) and is
        // reported as an error, not thrown.
        var result = await tool.InvokeAsync();

        Assert.Contains("bad path", result?.ToString());
        gate.Received().ReportToolResult(Arg.Any<string>(), Arg.Is<string>(s => s.Contains("bad path")), true);
    }

    [Fact]
    public async Task Invoke_WhenTheToolThrowsALargeMessage_ReturnsAndReportsAnExplicitWarning()
    {
        var largeMessage = string.Join('\n', Enumerable.Range(1, 20_000).Select(index => $"error-{index:D6}"));
        AIFunction inner = AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException(largeMessage)), "myTool");
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), "myTool", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ToolApprovalResult.Allow);
        var tool = new GatedTool(inner, gate);

        var result = (await tool.InvokeAsync())?.ToString();

        Assert.StartsWith("Tool result truncated by Cockpit.", result);
        Assert.Contains("Refine or paginate the tool call", result);
        gate.Received().ReportToolResult(Arg.Any<string>(), result!, true);
    }

    private static HttpClientTransport _TransportTo(InProcessMcpHttpServer server) =>
        new(new HttpClientTransportOptions { Endpoint = new Uri(server.Url) });
}
