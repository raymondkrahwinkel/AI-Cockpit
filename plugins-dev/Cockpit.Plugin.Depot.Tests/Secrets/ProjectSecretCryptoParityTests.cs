using System.Security.Cryptography;
using HostProtectionException = Cockpit.Core.Secrets.SecretProtectionException;
using HostProtector = Cockpit.Core.Secrets.SecretProtector;
using HostSecretFields = Cockpit.Core.Secrets.SecretFields;
using HostSecretKey = Cockpit.Core.Secrets.SecretKey;
using PluginHeuristic = Cockpit.Plugin.Depot.Secrets.SensitiveFieldNameHeuristic;
using PluginProtectionException = Cockpit.Plugin.Depot.Secrets.ProjectSecretProtectionException;
using PluginProtector = Cockpit.Plugin.Depot.Secrets.ProjectSecretProtector;
using PluginSecretKey = Cockpit.Plugin.Depot.Secrets.ProjectSecretKey;

namespace Cockpit.Plugin.Depot.Tests.Secrets;

// AC-607: the host's real Secrets primitives and this plugin's mirrored copies must agree — a plugin cannot
// reference Cockpit.Core (AC-244), the same constraint ProjectResourceSecretPathParityTests already works
// around. Change either copy and this goes red, whichever side changed.
public class ProjectSecretCryptoParityTests
{
    // Same password, salt and iterations derive the byte-identical key, and a value protected on either side
    // unprotects to the same plaintext on the other.
    [Fact]
    public void DeriveAndProtect_AgreeOnBothSides_InEitherDirection()
    {
        var salt = HostSecretKey.NewSalt();
        var hostKey = HostSecretKey.Derive("correct horse battery staple", salt, HostSecretKey.DefaultIterations);
        var pluginKey = PluginSecretKey.Derive("correct horse battery staple", salt, PluginSecretKey.DefaultIterations);
        Assert.Equal(hostKey, pluginKey);

        var fromHost = new HostProtector(hostKey).Protect("SensitiveFields.Api key", "plaintext-value");
        Assert.Equal("plaintext-value", new PluginProtector(hostKey).Unprotect("SensitiveFields.Api key", fromHost));

        var fromPlugin = new PluginProtector(hostKey).Protect("SensitiveFields.Api key", "plaintext-value");
        Assert.Equal("plaintext-value", new HostProtector(hostKey).Unprotect("SensitiveFields.Api key", fromPlugin));
    }

    [Fact]
    public void Unprotect_WrongKeyOnEitherSide_ThrowsItsOwnProtectionException()
    {
        var right = HostSecretKey.Derive("right-password", HostSecretKey.NewSalt(), HostSecretKey.DefaultIterations);
        var wrong = RandomNumberGenerator.GetBytes(32);
        var ciphertext = new HostProtector(right).Protect("path", "secret");

        Assert.Throws<PluginProtectionException>(() => new PluginProtector(wrong).Unprotect("path", ciphertext));
        Assert.Throws<HostProtectionException>(() => new HostProtector(wrong).Unprotect("path", ciphertext));
    }

    [Theory]
    [InlineData("token", true)]
    [InlineData("ApiKey", true)]
    [InlineData("api_key", true)]
    [InlineData("webhookUrl", true)]
    [InlineData("Secret", true)]
    [InlineData("password", true)]
    [InlineData("someFutureField", false)]
    [InlineData("Description", false)]
    [InlineData("Name", false)]
    public void SensitiveFieldNameHeuristic_AgreesWithHostSecretFieldsByName(string name, bool expected)
    {
        Assert.Equal(expected, HostSecretFields.ByName.IsSecret(name));
        Assert.Equal(expected, PluginHeuristic.IsSecretName(name));
        Assert.Equal(HostSecretFields.ByName.IsSecret(name), PluginHeuristic.IsSecretName(name));
    }
}
