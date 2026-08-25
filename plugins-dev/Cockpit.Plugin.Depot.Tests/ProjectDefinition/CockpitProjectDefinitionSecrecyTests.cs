using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// Pins the rule that a sensitive value never leaves this machine in the clear (Raymond, 2026-08-02): a field
// carrying one either travels encrypted under a project password, or it does not travel at all. Depot is a shared
// server — a colleague, and whoever administers the instance, can read what lands there.
// These tests do not implement that rule; they make it impossible to break it by accident. The definition carries
// no sensitive field today, and the assertion below is what turns that from a coincidence into a decision.
public class CockpitProjectDefinitionSecrecyTests
{
    // The complete set of fields the shared definition writes. Adding one is a deliberate act: this test goes red,
    // and whoever added it has to say here whether the new field can carry a secret. A project's
    // `AdditionalInfo` rows are the ones to watch — they carry `IsSecret` and are stored encrypted
    // locally, so putting them on the wire unencrypted would undo exactly what that flag is for.
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
        // AC-607 (deliberate act, as this test's own comment demands): a project's IsSecret AdditionalInfo rows
        // now DO travel, but only ever as ciphertext under the project's data key — see
        // CockpitProjectSensitiveFieldFilter.Apply, the only place that builds SensitiveFields entries.
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

    // AC-607 review finding 5: the definition now deliberately DOES carry a secret-derived row — SensitiveFields,
    // ciphertext only, never plaintext. This test does not guard against that; it guards against a plaintext
    // `AdditionalInfo`-shaped or `Secret`-named property specifically ever appearing again, which
    // `SensitiveFields`/`PasswordEnvelope` correctly do not trip (neither name contains "Secret" or
    // "AdditionalInfo").
    [Fact]
    public void Definition_CarriesNoSecretOrAdditionalInfoNamedProperty_OnlyTheEncryptedSensitiveFieldsRow()
    {
        var names = typeof(CockpitProjectDefinition).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("AdditionalInfo", StringComparison.OrdinalIgnoreCase));
    }

    // AC-618 acceptance criterion 3: a project's category is explicitly local (`Project.Category`, stored only
    // in `cockpit.json`) — sharing it here would let whoever shares a project impose their own filing on every
    // colleague who binds to it. Definition_CarriesOnlyFieldsClearedForSharing_SoAddingOneIsADeliberateAct
    // above already guards this by construction (the exhaustive whitelist would need editing), but this pins the
    // specific rule by name so it reads as a decision rather than an accident of that other test's coverage.
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

    // The gap is narrowed at the write seam, not closed: CockpitProjectDefinitionExtensionDataGuard (applied by
    // CockpitProjectDefinitionStore.WriteAsync, AC-607) refuses a secret-shaped, not-already-encrypted key at the
    // top level or one level of nested-object keys. It still forwards a secret-shaped value two-plus levels deep,
    // inside an array, inside an array of objects, or embedded as JSON-in-a-string (the exact last case the host's
    // own SecretJsonWalker handles and this guard deliberately does not — see
    // CockpitProjectDefinitionExtensionDataGuardTests for the 4 "still forwarded" cases pinned as a known, accepted
    // limitation, the same narrow-and-defensible tradeoff ProjectResourceSecretPathHeuristic already documents for
    // itself). `someFutureField` here matches no secret-name heuristic at all, so it correctly still forwards.
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
            // AC-246 (Raymond, 2026-08-02): a plain bool — "a row belongs here, without its reference" — cannot
            // itself carry a secret value the way a free-text field could. The field this guard actually exists to
            // catch (a text field a secret could hide in) is still exactly one: Reference, still governed by
            // ProjectResourceSecretPathHeuristic before Create ever builds a row, Placeholder row or not.
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
