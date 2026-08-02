using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

/// <summary>
/// Pins the rule that a sensitive value never leaves this machine in the clear (Raymond, 2026-08-02): a field
/// carrying one either travels encrypted under a project password, or it does not travel at all. Depot is a shared
/// server — a colleague, and whoever administers the instance, can read what lands there.
///
/// These tests do not implement that rule; they make it impossible to break it by accident. The definition carries
/// no sensitive field today, and the assertion below is what turns that from a coincidence into a decision.
/// </summary>
public class CockpitProjectDefinitionSecrecyTests
{
    /// <summary>
    /// The complete set of fields the shared definition writes. Adding one is a deliberate act: this test goes red,
    /// and whoever added it has to say here whether the new field can carry a secret. A project's
    /// <c>AdditionalInfo</c> rows are the ones to watch — they carry <c>IsSecret</c> and are stored encrypted
    /// locally, so putting them on the wire unencrypted would undo exactly what that flag is for.
    /// </summary>
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

    /// <summary>
    /// AC-618 acceptance criterion 3: a project's category is explicitly local (<c>Project.Category</c>, stored only
    /// in <c>cockpit.json</c>) — sharing it here would let whoever shares a project impose their own filing on every
    /// colleague who binds to it. <see cref="Definition_CarriesOnlyFieldsClearedForSharing_SoAddingOneIsADeliberateAct"/>
    /// above already guards this by construction (the exhaustive whitelist would need editing), but this pins the
    /// specific rule by name so it reads as a decision rather than an accident of that other test's coverage.
    /// </summary>
    [Fact]
    public void Definition_CarriesNoCategory_SinceCategoryIsAlwaysLocalToEachOperator()
    {
        var names = typeof(CockpitProjectDefinition).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Category", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The known gap, pinned so it is a documented limit rather than a surprise. <c>ExtensionData</c> exists to
    /// carry a newer Cockpit's fields through a read-then-write untouched (AC-244) — which means an older build
    /// forwards a field it cannot recognise, including one holding a secret a later version chose to share. Keeping
    /// forward compatibility and refusing unknown sensitive data are in direct tension here, and this build resolves
    /// it in favour of compatibility. Whatever design lands the project password has to close this: an unrecognised
    /// field is exactly the case a receiving build cannot judge for itself.
    /// </summary>
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
}
