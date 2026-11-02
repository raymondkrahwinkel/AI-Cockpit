namespace Cockpit.Infrastructure.Sessions;

internal static class ReadOnlyTurnToolPolicy
{
    public static bool IsReadOnlyTool(string toolName) =>
        toolName is "Read" or "Glob" or "Grep" or "LS" or "WebFetch" or "WebSearch"
            or "ListMcpResourcesTool" or "ReadMcpResourceTool";
}
