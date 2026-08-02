using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// On-disk shape of `mcpOverlay` in `.cockpit/project.json` (AC-244) — only which servers start ticked, by name. A project's own additional server definitions (command path, env vars) stay local; they do not travel.
public sealed class CockpitProjectMcpOverlayEntry
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Enabled { get; set; }

    // AC-244: whatever a newer Cockpit wrote here that this build does not know about, carried through untouched.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
