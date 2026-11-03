using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectSensitiveFieldFilterTests
{

    // AC-607 review finding 9 at the end: the AAD binding the ciphertext relies on. Encrypted at one field's path, the same data key
    // cannot decrypt it back at a different field's path.
    [Fact]
    public void Apply_WithDataKey_SecretRowIsEncryptedAndRecoverableWithTheRightKeyAtItsOwnPathOnly()
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
        Assert.NotNull(rightKey);
        var recovered = new Cockpit.Plugin.Depot.Secrets.ProjectSecretProtector(rightKey)
            .Unprotect($"SensitiveFields.{entry.Label}", entry.Value);
        Assert.Equal("s3cr3t-value", recovered);

        var protector = new Cockpit.Plugin.Depot.Secrets.ProjectSecretProtector(dataKey);
        Assert.Throws<Cockpit.Plugin.Depot.Secrets.ProjectSecretProtectionException>(
            () => protector.Unprotect("SensitiveFields.A different label", entry.Value));
    }
}
