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

    [Fact]
    public void Definition_CarriesNoAdditionalInfo_SinceThoseRowsAreWhereSecretsLive()
    {
        var names = typeof(CockpitProjectDefinition).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("AdditionalInfo", StringComparison.OrdinalIgnoreCase));
    }

    // The known gap, pinned so it is a documented limit rather than a surprise. `ExtensionData` exists to
    // carry a newer Cockpit's fields through a read-then-write untouched (AC-244) — which means an older build
    // forwards a field it cannot recognise, including one holding a secret a later version chose to share. Keeping
    // forward compatibility and refusing unknown sensitive data are in direct tension here, and this build resolves
    // it in favour of compatibility. Whatever design lands the project password has to close this: an unrecognised
    // field is exactly the case a receiving build cannot judge for itself.
    [Fact]
    public void ExtensionData_ForwardsUnknownFieldsUnread_WhichIsTheGapAnEncryptionDesignMustClose()
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
        ];

        var actual = typeof(CockpitProjectResourceEntry)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(cleared.OrderBy(name => name, StringComparer.Ordinal), actual);
    }
}
