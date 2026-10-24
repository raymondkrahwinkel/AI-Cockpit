using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Cockpit.Plugin.Depot.Tests;

// Contract (AC-1642): the plugin itself talks to Depot over REST and the event channel only. MCP is the agent session's,
// through GetMcpServers; neither of the plugin's assemblies may so much as reference a host MCP tool call.
public sealed class DepotMcpBoundaryTests
{
    [Fact]
    public void Neither_plugin_assembly_references_a_host_mcp_tool_call()
    {
        string[] forbidden = ["CallMcpToolAsync", "ProbeMcpToolAsync"];
        var assemblies = new[]
        {
            typeof(DepotPlugin).Assembly.Location,
            Path.Combine(AppContext.BaseDirectory, "Cockpit.Plugin.Depot.UI.dll"),
        };

        var calls = new List<string>();
        foreach (var path in assemblies)
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            var metadata = reader.GetMetadataReader();
            foreach (var handle in metadata.MemberReferences)
            {
                var name = metadata.GetString(metadata.GetMemberReference(handle).Name);
                if (forbidden.Contains(name))
                {
                    calls.Add($"{Path.GetFileName(path)}: {name}");
                }
            }
        }

        Assert.Empty(calls);
    }
}
