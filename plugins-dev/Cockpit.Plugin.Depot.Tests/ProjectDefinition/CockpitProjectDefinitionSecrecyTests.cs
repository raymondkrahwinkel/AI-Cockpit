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

    // AC-607: the definition deliberately DOES carry a secret-derived row (SensitiveFields, ciphertext only).
    // This guards against a plaintext `AdditionalInfo`-shaped or `Secret`-named property ever appearing —
    // `SensitiveFields`/`PasswordEnvelope` correctly do not trip either check.
    [Fact]
    public void Definition_CarriesNoSecretOrAdditionalInfoNamedProperty_OnlyTheEncryptedSensitiveFieldsRow()
    {
        var names = typeof(CockpitProjectDefinition).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("AdditionalInfo", StringComparison.OrdinalIgnoreCase));
    }

    // AC-618 criterion 3: a project's category is explicitly local (`Project.Category`, stored only in
    // `cockpit.json`) — sharing it would let whoever shares a project impose their own filing on every
    // colleague. Pinned by name so it reads as a decision, not an accident of the whitelist test above.
    [Fact]
    public void Definition_CarriesNoCategory_SinceCategoryIsAlwaysLocalToEachOperator()
    {
        var names = typeof(CockpitProjectDefinition).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Category", StringComparison.OrdinalIgnoreCase));
    }

    // AC-1071 acceptance criterion 4: a project's assistant is explicitly local, for the same reason its category
    // is — sharing it would impose one operator's persona on every colleague who binds the project, which is the
    // exact complaint this ticket came from. Pinned by name so it reads as a decision, not as coverage.
    [Fact]
    public void Definition_CarriesNoAssistant_SinceTheAssistantIsAlwaysLocalToEachOperator()
    {
        var names = typeof(CockpitProjectDefinition).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Assistant", StringComparison.OrdinalIgnoreCase));
    }

    // CockpitProjectDefinitionExtensionDataGuard (AC-607) narrows the gap, doesn't close it: it refuses a
    // secret-shaped key only at the top level or one nesting level (known accepted limitation, see
    // CockpitProjectDefinitionExtensionDataGuardTests). `someFutureField` matches no heuristic, still forwards.
    [Fact]
    public void ExtensionData_ForwardsUnknownFieldsUnread_WhichAC607NarrowsButDoesNotFullyClose()
    {
        const string fromANewerCockpit = """{"schemaVersion":1,"name":"probe","someFutureField":"forwarded as-is"}""";

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(fromANewerCockpit, out var definition, out _));
        Assert.Contains("someFutureField", CockpitProjectDefinitionJson.Serialize(definition!));
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
