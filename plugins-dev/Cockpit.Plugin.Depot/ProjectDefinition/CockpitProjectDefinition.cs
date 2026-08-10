using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// On-disk shape of `.cockpit/project.json` (AC-244) — the portable half of a project definition that lives
// in a Depot project. Mirrors `Cockpit.Infrastructure.Configuration.ProjectEntry`'s idiom without referencing it: a plugin project never references Cockpit.Core.
public sealed class CockpitProjectDefinition
{
    // Forward-compat marker (AC-242): any value is accepted without failing deserialization.
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

    // Path of the shared logo blob relative to the Depot project root, e.g. `.cockpit/logo.png`.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logo { get; set; }

    // AC-607: the project's IsSecret AdditionalInfo rows, each encrypted under the project's data key — see
    // CockpitProjectSensitiveFieldFilter.Apply, the only place that builds this list. Null for a project with
    // none, or with no project password set (a secret row without a password is dropped, not written unencrypted).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CockpitProjectSensitiveFieldEntry>? SensitiveFields { get; set; }

    // AC-607: how the data key that unlocks SensitiveFields is recovered, from either the project password or
    // its recovery code — see CockpitProjectPasswordEnvelopeFactory. Null until a project password is first set.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CockpitProjectPasswordEnvelope? PasswordEnvelope { get; set; }

    // AC-244: whatever a newer Cockpit wrote at the top level that this build does not know about, carried through
    // a read-then-write untouched — System.Text.Json fills and re-emits this on its own, no merge code needed.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
