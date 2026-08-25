using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugin.Depot.Secrets;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// AC-607 acceptance criteria 1, 2 and 5: what actually lands in the bytes CockpitProjectDefinitionJson.Serialize
// writes for `.cockpit/project.json`, not just what CockpitProjectSensitiveFieldFilter reports in isolation.
public class CockpitProjectDefinitionSensitiveFieldsTests
{
    // AC1: without a project password, a secret row's plaintext must never reach the serialized bytes. Proved
    // red-without-fix manually (see AC-607-progress.md) by temporarily making
    // CockpitProjectSensitiveFieldFilter.Apply a no-op, then restoring the fix.
    [Fact]
    public void Serialize_NoDataKey_SecretRowPlaintextNeverAppearsInTheBytes()
    {
        const string credential = "row-credential-that-must-not-survive";
        var filterResult = CockpitProjectSensitiveFieldFilter.Apply(
            [("Deploy token", credential, true), ("Repository", "https://example.com/repo", false)], dataKey: null);

        var definition = new CockpitProjectDefinition
        {
            Name = "probe",
            SensitiveFields = filterResult.Encrypted.Count == 0 ? null : [.. filterResult.Encrypted],
        };
        var json = CockpitProjectDefinitionJson.Serialize(definition);

        Assert.DoesNotContain(credential, json, StringComparison.Ordinal);
        Assert.DoesNotContain("SensitiveFields", json, StringComparison.Ordinal);
        var dropped = Assert.Single(filterResult.Dropped);
        Assert.Equal("Deploy token", dropped.Label);
    }

    // AC2: with a project password, the wire ciphertext is unreadable without it — not merely base64 or some
    // other reversible non-crypto transform — and the right password recovers the exact original plaintext.
    [Fact]
    public void Serialize_WithDataKey_CiphertextOnTheWireIsUnreadableWithoutThePassword()
    {
        const string credential = "row-credential-that-must-not-survive";
        var (envelope, dataKey, _) = CockpitProjectPasswordEnvelopeFactory.Create("project-password");
        var filterResult = CockpitProjectSensitiveFieldFilter.Apply([("Deploy token", credential, true)], dataKey);

        var definition = new CockpitProjectDefinition
        {
            Name = "probe",
            SensitiveFields = [.. filterResult.Encrypted],
            PasswordEnvelope = envelope,
        };
        var json = CockpitProjectDefinitionJson.Serialize(definition);

        Assert.DoesNotContain(credential, json, StringComparison.Ordinal);

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var roundTripped, out _));
        var entry = Assert.Single(roundTripped!.SensitiveFields!);

        var wrongKey = CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(roundTripped.PasswordEnvelope!, "not-the-password");
        Assert.Null(wrongKey);

        var otherProtector = new ProjectSecretProtector(new byte[32]);
        Assert.Throws<ProjectSecretProtectionException>(
            () => otherProtector.Unprotect($"SensitiveFields.{entry.Label}", entry.Value));

        var rightKey = CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(roundTripped.PasswordEnvelope!, "project-password");
        Assert.NotNull(rightKey);
        var recovered = new ProjectSecretProtector(rightKey!).Unprotect($"SensitiveFields.{entry.Label}", entry.Value);
        Assert.Equal(credential, recovered);
    }

    // AC5: a project with no IsSecret rows behaves exactly as before this ticket — nothing new on the wire.
    [Fact]
    public void Serialize_NoSensitiveFieldsOrEnvelope_OmitsBothKeysEntirely()
    {
        var definition = new CockpitProjectDefinition { Name = "probe" };

        var json = CockpitProjectDefinitionJson.Serialize(definition);

        Assert.DoesNotContain("sensitiveFields", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passwordEnvelope", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(definition.SensitiveFields);
        Assert.Null(definition.PasswordEnvelope);
    }
}
