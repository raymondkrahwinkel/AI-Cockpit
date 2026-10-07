using Microsoft.Extensions.AI;
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
    public async Task Invoke_WhenTheToolReturnsAContentList_ReturnsItsTextNotItsTypeName()
    {
        var gate = Substitute.For<IToolApprovalGate>();
        gate.RequestApprovalAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ToolApprovalResult.Allow);
        var tool = new GatedTool(new ContentListTool(), gate);

        var result = await tool.InvokeAsync();

        Assert.Equal("hello", result);
    }

    // What McpClientTool hands back for a result of several content blocks (AC-1492).
    private sealed class ContentListTool : AIFunction
    {
        public override string Name => "contentList";

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            new(new AIContent[] { new TextContent("hello") });
    }

    private static HttpClientTransport _TransportTo(InProcessMcpHttpServer server) =>
        new(new HttpClientTransportOptions { Endpoint = new Uri(server.Url) });
}
