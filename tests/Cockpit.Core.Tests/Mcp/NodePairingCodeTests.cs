using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The number both screens show during a pairing (AC-792). Its whole job is to differ when the certificate differs,
/// because that is what a comparison by eye catches: a machine in the middle forwards the nonce unchanged but
/// cannot forward a certificate it has no private key for.
/// </summary>
public class NodePairingCodeTests
{
    private const string NodeFingerprint = "3F1A9C4E5B6D7089AB12CD34EF56789012345678ABCDEF1234567890ABCDEF12";
    private const string ImpostorFingerprint = "0011223344556677889900AABBCCDDEEFF00112233445566778899AABBCCDDEE";

    [Fact]
    public void Derive_IsTheSameOnBothSides_ForTheSameNonceAndCertificate() =>
        Assert.Equal(
            NodePairingCode.Derive("A1B2C3", NodeFingerprint),
            NodePairingCode.Derive("A1B2C3", NodeFingerprint));

    [Fact]
    public void Derive_DiffersWhenTheCertificateDiffers_WhichIsWhatTheOperatorSees() =>
        Assert.NotEqual(
            NodePairingCode.Derive("A1B2C3", NodeFingerprint),
            NodePairingCode.Derive("A1B2C3", ImpostorFingerprint));

    [Fact]
    public void Derive_DiffersPerPairing_SoOneCodeSaysNothingAboutTheNext() =>
        Assert.NotEqual(
            NodePairingCode.Derive("A1B2C3", NodeFingerprint),
            NodePairingCode.Derive("D4E5F6", NodeFingerprint));

}
