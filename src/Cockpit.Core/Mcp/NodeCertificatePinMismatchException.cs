namespace Cockpit.Core.Mcp;

// The node answered with a different TLS certificate than the one pinned when it was paired (AC-792).
//
// Raised from the certificate-validation callback rather than returned as a plain "not trusted", so the reason
// survives as the inner exception of the `HttpRequestException` the caller sees. A pin mismatch and a wrong bearer
// token are different events — one is a credential the operator can fix, the other is the machine at that address
// not being the machine they paired with — and a transport failure that only said "TLS error" would put them in
// the same bucket. That is the distinction criterion 8 asks for.
public sealed class NodeCertificatePinMismatchException(string expectedFingerprint, string? presentedFingerprint)
    : Exception($"This node presented certificate {presentedFingerprint ?? "(none)"}, not the {expectedFingerprint} pinned when it was paired. Either the node was re-installed, or something else is answering at that address.")
{
    public string ExpectedFingerprint { get; } = expectedFingerprint;

    public string? PresentedFingerprint { get; } = presentedFingerprint;
}
