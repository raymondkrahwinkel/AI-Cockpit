using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>
/// On-disk shape of <c>.cockpit/project.json</c> (AC-244) — the portable half of a project definition that lives
/// in a Depot project. Mirrors <c>Cockpit.Infrastructure.Configuration.ProjectEntry</c>'s idiom without referencing it: a plugin project never references Cockpit.Core.
/// </summary>
public sealed class CockpitProjectDefinition
{
    /// <summary>Forward-compat marker (AC-242): any value is accepted without failing deserialization.</summary>
    public int SchemaVersion { get; set; } = CockpitProjectDefinitionJson.CurrentSchemaVersion;

    public string Name { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GitUrl { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BehaviorPrompt { get; set; }

    public bool IsolateInWorktreeByDefault { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CockpitProjectMcpOverlayEntry? McpOverlay { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CockpitProjectResourceEntry>? Resources { get; set; }

    /// <summary>Path of the shared logo blob relative to the Depot project root, e.g. <c>.cockpit/logo.png</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logo { get; set; }

    // AC-244: whatever a newer Cockpit wrote at the top level that this build does not know about, carried through
    // a read-then-write untouched — System.Text.Json fills and re-emits this on its own, no merge code needed.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
