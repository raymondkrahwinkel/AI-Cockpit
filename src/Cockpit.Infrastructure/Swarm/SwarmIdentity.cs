using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Swarm;

// AC-1915: this Cockpit's swarm identity is the key pair behind its node certificate; there is no second key
// store. A reinstall, or anything else that mints a new certificate key, gives a new member id.
internal sealed class SwarmIdentity(NodeSelfSignedCertificate certificate) : ISingletonService
{
    public SwarmMemberId MemberId => SwarmMemberId.FromPublicKey(certificate.Value.PublicKey.ExportSubjectPublicKeyInfo());

    public SwarmRecord Sign(JsonElement payload)
    {
        using var key = certificate.Value.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("The node certificate carries no ECDSA private key to sign swarm records with.");
        return SwarmRecord.Sign(payload, key);
    }
}
