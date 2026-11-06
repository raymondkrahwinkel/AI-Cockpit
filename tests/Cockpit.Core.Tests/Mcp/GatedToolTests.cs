using Microsoft.Extensions.AI;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="GatedTool"/>: an MCP tool runs only after the approval gate says yes — an approval invokes
/// the underlying tool, a denial returns a refusal without ever running it (#26 human-in-the-loop).
/// </summary>
public class GatedToolTests
{
    [Fact]
    public async Task Invoke_UsesCanonicalPermissions_AndNeverSharesAnAllowBetweenServers()
    {
        var calls = 0;
        var names = new McpToolNames();
        var alias = names.ForTool("coding", "write");
        var canonical = names.ToCanonical(alias);
        var otherAlias = names.ForTool("other", "write");
        var reported = new List<string>();
        AIFunction inner = AIFunctionFactory.Create(() => { calls++; return "the result"; }, alias);
        var gate = new SessionToolApprovalGate((_, name, _) => reported.Add(name), (_, _, _) => throw new InvalidOperationException("Delegation must not prompt."), (_, _, _) => { })
        {
            ToolNames = names,
            ToolClasses = new Dictionary<string, ToolPermissionClass>
            {
                [canonical] = ToolPermissionClass.Write,
                [names.ToCanonical(otherAlias)] = ToolPermissionClass.Write,
            },
        };
        gate.SetDelegatedGate("default", []);
        var tool = new GatedTool(inner, gate);

        var result = await tool.InvokeAsync();

        Assert.Equal(0, calls);
        Assert.Contains("does not permit", result?.ToString());
        Assert.Equal("mcp__coding__write", Assert.Single(reported));

        gate.SetDelegatedGate("default", [canonical]);
        Assert.Equal("the result", (await tool.InvokeAsync())?.ToString());
        var other = new GatedTool(AIFunctionFactory.Create(() => { calls++; return "wrong server"; }, otherAlias), gate);
        Assert.Contains("does not permit", (await other.InvokeAsync())?.ToString());
        Assert.Equal(1, calls);
    }
}
