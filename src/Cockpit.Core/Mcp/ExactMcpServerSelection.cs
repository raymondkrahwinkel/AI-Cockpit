namespace Cockpit.Core.Mcp;

public sealed class ExactMcpServerSelection(IEnumerable<string> names)
    : HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
{
    public bool AssistantManaged { get; init; }
}
