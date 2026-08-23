namespace Cockpit.Core.Mcp;

// AC-792: the node answered with a different TLS certificate than the one pinned at pairing time. Raised from
// the certificate-validation callback (surviving as the inner exception of `HttpRequestException`) so a pin
// mismatch stays distinguishable from a wrong bearer token or a generic "TLS error".
public sealed class NodeCertificatePinMismatchException(string expectedFingerprint, string? presentedFingerprint)
    : Exception($"This node presented certificate {presentedFingerprint ?? "(none)"}, not the {expectedFingerprint} pinned when it was paired. Either the node was re-installed, or something else is answering at that address.")
{
    public string ExpectedFingerprint { get; } = expectedFingerprint;

    public string? PresentedFingerprint { get; } = presentedFingerprint;
}
