using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// What flows between steps (#69) — and the thing my first model did not have at all. A step does not receive a
/// signal that it is its turn; it receives <em>items</em>, works on them, and passes items on. A "run a command"
/// step hands its output to the next one; a decision reads a field to decide which way to go. Without this a
/// workflow can only be a chain of unrelated actions.
/// <para>
/// One JSON object per item, which is the smallest thing that carries structure. How a node's parameters refer to
/// that data (n8n writes <c>{{ $json.x }}</c>) is deliberately not decided here — the cockpit will have its own,
/// and it belongs with the parameter editor, not with the data.
/// </para>
/// </summary>
public sealed record WorkflowItem(JsonObject Json)
{
    public static WorkflowItem Empty() => new(new JsonObject());

    /// <summary>One item carrying a single named value — what a trigger usually starts a run with.</summary>
    public static WorkflowItem Of(string name, string value) => new(new JsonObject { [name] = value });

    /// <summary>An item out of the plain fields a contributed step handed back — the SDK deals in strings so a plugin never has to reference this one.</summary>
    public static WorkflowItem Of(IReadOnlyDictionary<string, string> fields)
    {
        var json = new JsonObject();
        foreach (var (key, value) in fields)
        {
            json[key] = value;
        }

        return new WorkflowItem(json);
    }
}
