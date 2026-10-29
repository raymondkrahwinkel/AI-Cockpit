using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Swarm;

namespace Cockpit.Core.Tests.Swarm;

/// <summary>
/// Layer 2, protocol/authorization (AC-1915). A record signed through the node certificate verifies against the
/// member register built from a renewed certificate around the same key, and stops verifying once its payload
/// is touched or its signature comes from a key other than the one behind the member id.
/// </summary>
public class SwarmRecordSignatureTests
{
    [Theory]
    [InlineData("tampered-payload")]
    [InlineData("foreign-key")]
    public void Verify_AcceptsOnlyThePayloadSignedByTheKeyBehindTheMemberId(string forgery)
    {
        var originalPath = Path.Combine(Path.GetTempPath(), $"swarm-identity-{Guid.NewGuid():N}.pfx");
        var renewedPath = Path.Combine(Path.GetTempPath(), $"swarm-identity-{Guid.NewGuid():N}.pfx");
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllBytes(originalPath, _SelfSignedPkcs12(key, DateTimeOffset.UtcNow.AddDays(-2)));
            File.WriteAllBytes(renewedPath, _SelfSignedPkcs12(key, DateTimeOffset.UtcNow.AddDays(-1)));

            using var original = new NodeSelfSignedCertificate(originalPath);
            using var originalAgain = new NodeSelfSignedCertificate(originalPath);
            using var renewed = new NodeSelfSignedCertificate(renewedPath);
            var member = new SwarmIdentity(original);

            // Criterion 1: same machine twice, and a renewed certificate around the same key, is one member.
            Assert.NotEqual(original.Fingerprint, renewed.Fingerprint);
            Assert.Equal(member.MemberId, new SwarmIdentity(originalAgain).MemberId);
            Assert.Equal(member.MemberId, new SwarmIdentity(renewed).MemberId);

            var register = new Dictionary<SwarmMemberId, byte[]> { [member.MemberId] = renewed.Value.PublicKey.ExportSubjectPublicKeyInfo() };
            var record = member.Sign(JsonSerializer.SerializeToElement(new { member = "alpha", sequence = 1 }));
            Assert.True(record.Verify(register.GetValueOrDefault));

            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var strangerKey = stranger.ExportSubjectPublicKeyInfo();
            var (forged, publicKeyOf) = forgery switch
            {
                // Criterion 2: one byte of the payload changed, checked against the honest register.
                "tampered-payload" => (
                    record with { Payload = JsonDocument.Parse(record.Payload.GetRawText().Replace("alpha", "alphb", StringComparison.Ordinal)).RootElement },
                    register.GetValueOrDefault),

                // Criterion 3: a stranger signs under A's id and hands over its own key as the one to check with.
                "foreign-key" => (
                    record with { Signatures = [new SwarmSignature(member.MemberId, SwarmRecord.Sign(record.Payload, stranger).Signatures[0].Signature)] },
                    new Func<SwarmMemberId, byte[]?>(_ => strangerKey)),
                _ => throw new ArgumentOutOfRangeException(nameof(forgery), forgery, null),
            };

            Assert.False(forged.Verify(publicKeyOf));
        }
        finally
        {
            File.Delete(originalPath);
            File.Delete(renewedPath);
        }
    }

    private static byte[] _SelfSignedPkcs12(ECDsa key, DateTimeOffset notBefore)
    {
        var request = new CertificateRequest("CN=cockpit-node", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(notBefore, notBefore.AddYears(1));
        return certificate.Export(X509ContentType.Pfx);
    }
}
