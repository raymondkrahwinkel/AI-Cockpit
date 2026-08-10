using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// One AdditionalInfo row that is IsSecret, as it travels to Depot (AC-607): Value is always ciphertext
// (`enc:v1:...`), never plaintext — the only place that builds one is CockpitProjectSensitiveFieldFilter.Apply.
public sealed class CockpitProjectSensitiveFieldEntry
{
    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    // AC-244 idiom: a newer build's fields on this row are carried through untouched on a read-then-write.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
