using System.Text.Json.Nodes;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// What of a step's data is kept in the run history. A run is stored, and a command that printed a megabyte would
/// otherwise put that megabyte in the settings file — twenty runs of it. So values are cut at a length a human can
/// still read, and the cut is visible: a value ending in an ellipsis is a value that was longer.
/// </summary>
internal static class RunItems
{
    private const int MaxValue = 2000;

    public static IReadOnlyList<JsonObject> Keep(IReadOnlyList<WorkflowItem> items) =>
        items.Select(item =>
        {
            var kept = new JsonObject();
            foreach (var (key, value) in item.Json)
            {
                var text = value?.ToString() ?? string.Empty;
                kept[key] = text.Length > MaxValue ? string.Concat(text.AsSpan(0, MaxValue), "…") : text;
            }

            return kept;
        }).ToList();
}
