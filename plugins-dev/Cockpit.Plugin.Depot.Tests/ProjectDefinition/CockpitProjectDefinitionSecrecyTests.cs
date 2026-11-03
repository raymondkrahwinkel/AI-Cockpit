using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// Pins the rule that a sensitive value never leaves this machine in the clear: a field carrying one either
// travels encrypted under a project password, or not at all — Depot is a shared server, readable by a
// colleague or admin. These tests turn "no sensitive field today" from coincidence into an enforced decision.
public class CockpitProjectDefinitionSecrecyTests
{
    // The complete set of fields the shared definition writes. Adding one is a deliberate act: this test goes red,
    // and whoever added it has to say here whether it can carry a secret — watch `AdditionalInfo` rows
    // especially, since they carry `IsSecret` and are stored encrypted locally.
    private static readonly string[] _FieldsClearedForSharing =
    [
        nameof(CockpitProjectDefinition.SchemaVersion),
        nameof(CockpitProjectDefinition.Name),
        nameof(CockpitProjectDefinition.Description),
        nameof(CockpitProjectDefinition.GitUrl),
        nameof(CockpitProjectDefinition.BehaviorPrompt),
        nameof(CockpitProjectDefinition.IsolateInWorktreeByDefault),
        nameof(CockpitProjectDefinition.McpOverlay),
        nameof(CockpitProjectDefinition.Resources),
        nameof(CockpitProjectDefinition.Logo),
        nameof(CockpitProjectDefinition.ExtensionData),
        // AC-607: a project's IsSecret AdditionalInfo rows now DO travel, but only as ciphertext under the
        // project's data key — see CockpitProjectSensitiveFieldFilter.Apply.
        nameof(CockpitProjectDefinition.SensitiveFields),
        nameof(CockpitProjectDefinition.PasswordEnvelope),
    ];

    [Fact]
    public void Definition_CarriesOnlyFieldsClearedForSharing_SoAddingOneIsADeliberateAct()
    {
        var actual = typeof(CockpitProjectDefinition)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(_FieldsClearedForSharing.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

    // AC-607: the definition deliberately DOES carry a secret-derived row (SensitiveFields, ciphertext only), never a plaintext
    // `AdditionalInfo`-shaped or `Secret`-named one. AC-618 criterion 3 and AC-1071 criterion 4: a project's category and its assistant
    // are local to each operator, and sharing either would impose one operator's filing or persona on every colleague. Pinned by name.
    [Fact]
    public void Definition_CarriesNoSecretAdditionalInfoCategoryOrAssistant_OnlyTheEncryptedSensitiveFieldsRow()
    {
        var names = typeof(CockpitProjectDefinition).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("AdditionalInfo", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Category", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Assistant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResourceEntry_CarriesOnlyFieldsClearedForSharing_SoARowCannotGrowASecretUnnoticed()
    {
        string[] cleared =
        [
            nameof(CockpitProjectResourceEntry.Role),
            nameof(CockpitProjectResourceEntry.Reference),
            nameof(CockpitProjectResourceEntry.Label),
            nameof(CockpitProjectResourceEntry.Portability),
            nameof(CockpitProjectResourceEntry.ExtensionData),
            // AC-246: a plain bool — "a row belongs here, without its reference" — cannot itself carry a
            // secret the way a free-text field could. Reference remains the one field this guard exists for,
            // still governed by ProjectResourceSecretPathHeuristic before Create ever builds a row.
            nameof(CockpitProjectResourceEntry.Placeholder),
        ];

        var actual = typeof(CockpitProjectResourceEntry)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(cleared.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

    // AC-607 review finding 4: the same reflection-whitelist pinning as ResourceEntry above, for the 3 new wire
    // types this ticket introduced — so a row growing an undeclared property (a plaintext fallback field, say)
    // goes red here rather than by inspection.
    [Fact]
    public void SensitiveFieldEntry_CarriesOnlyFieldsClearedForSharing_SoARowCannotGrowAPlaintextFallbackUnnoticed()
    {
        string[] cleared =
        [
            nameof(CockpitProjectSensitiveFieldEntry.Label),
            nameof(CockpitProjectSensitiveFieldEntry.Value),
            nameof(CockpitProjectSensitiveFieldEntry.ExtensionData),
        ];

        var actual = typeof(CockpitProjectSensitiveFieldEntry)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(cleared.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void PasswordEnvelope_CarriesOnlyFieldsClearedForSharing_SoItCannotGrowAPlaintextFallbackUnnoticed()
    {
        string[] cleared =
        [
            nameof(CockpitProjectPasswordEnvelope.Kdf),
            nameof(CockpitProjectPasswordEnvelope.Iterations),
            nameof(CockpitProjectPasswordEnvelope.Password),
            nameof(CockpitProjectPasswordEnvelope.Recovery),
        ];

        var actual = typeof(CockpitProjectPasswordEnvelope)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(cleared.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void KeyWrapper_CarriesOnlyFieldsClearedForSharing_SoItCannotGrowAPlaintextFallbackUnnoticed()
    {
        string[] cleared =
        [
            nameof(CockpitProjectKeyWrapper.Salt),
            nameof(CockpitProjectKeyWrapper.WrappedDataKey),
        ];

        var actual = typeof(CockpitProjectKeyWrapper)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(cleared.OrderBy(name => name, StringComparer.Ordinal), actual);
    }
}
