using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectSensitiveFieldFilterTests
{
    [Fact]
    public void Apply_NoDataKey_EverySecretRowIsDroppedWithAReason()
    {
        var result = CockpitProjectSensitiveFieldFilter.Apply(
            [("Deploy token", "s3cr3t", true), ("Repository", "https://example.com", false)], dataKey: null);

        Assert.Empty(result.Encrypted);
        var dropped = Assert.Single(result.Dropped);
        Assert.Equal("Deploy token", dropped.Label);
        Assert.False(string.IsNullOrWhiteSpace(dropped.Reason));
    }

    [Fact]
    public void Apply_WithDataKey_SecretRowIsEncryptedAndRecoverableWithTheRightKeyOnly()
    {
        var (envelope, dataKey, _) = CockpitProjectPasswordEnvelopeFactory.Create("project-password");

        var result = CockpitProjectSensitiveFieldFilter.Apply([("Deploy token", "s3cr3t-value", true)], dataKey);

        var entry = Assert.Single(result.Encrypted);
        Assert.Empty(result.Dropped);
        Assert.NotEqual("s3cr3t-value", entry.Value);
        Assert.StartsWith("enc:v1:", entry.Value, StringComparison.Ordinal);

        var wrongKey = CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "not-the-password");
        Assert.Null(wrongKey);

        var rightKey = CockpitProjectPasswordEnvelopeFactory.TryUnwrapWithPassword(envelope, "project-password");
        Assert.Equal(dataKey, rightKey);
        var recovered = new Cockpit.Plugin.Depot.Secrets.ProjectSecretProtector(rightKey!)
            .Unprotect($"SensitiveFields.{entry.Label}", entry.Value);
        Assert.Equal("s3cr3t-value", recovered);
    }

    [Fact]
    public void Apply_WrongKeyCannotDecryptTheCiphertext()
    {
        var (_, dataKey, _) = CockpitProjectPasswordEnvelopeFactory.Create("project-password");
        var result = CockpitProjectSensitiveFieldFilter.Apply([("Deploy token", "s3cr3t-value", true)], dataKey);
        var entry = Assert.Single(result.Encrypted);

        var (_, otherDataKey, _) = CockpitProjectPasswordEnvelopeFactory.Create("another-password");
        var wrongProtector = new Cockpit.Plugin.Depot.Secrets.ProjectSecretProtector(otherDataKey);

        Assert.Throws<Cockpit.Plugin.Depot.Secrets.ProjectSecretProtectionException>(
            () => wrongProtector.Unprotect($"SensitiveFields.{entry.Label}", entry.Value));
    }

    // AC-607 review finding 9: pins the AAD binding a sensitive field's ciphertext actually relies on — encrypted
    // at one field's path, the same data key cannot decrypt it back at a different field's path. Verified manually
    // by the reviewer before this; not previously pinned by any test in the suite.
    [Fact]
    public void Apply_CiphertextEncryptedForOneFieldsPath_CannotBeDecryptedAtAnotherFieldsPathWithTheSameKey()
    {
        var (_, dataKey, _) = CockpitProjectPasswordEnvelopeFactory.Create("project-password");
        var result = CockpitProjectSensitiveFieldFilter.Apply([("Deploy token", "s3cr3t-value", true)], dataKey);
        var entry = Assert.Single(result.Encrypted);

        var protector = new Cockpit.Plugin.Depot.Secrets.ProjectSecretProtector(dataKey);

        Assert.Throws<Cockpit.Plugin.Depot.Secrets.ProjectSecretProtectionException>(
            () => protector.Unprotect("SensitiveFields.A different label", entry.Value));
    }

    [Fact]
    public void Apply_NoSecretRows_ReturnsEmptyEncryptedAndEmptyDropped()
    {
        var result = CockpitProjectSensitiveFieldFilter.Apply(
            [("Repository", "https://example.com", false), ("Customer", "Acme BV", false)], dataKey: null);

        Assert.Empty(result.Encrypted);
        Assert.Empty(result.Dropped);
    }
}
