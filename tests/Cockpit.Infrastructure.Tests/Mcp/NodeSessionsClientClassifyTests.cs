using System.Net.Sockets;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// <see cref="NodeSessionsClient.Classify"/> (AC-796, criterion 2): what a node's own failure shapes read as, and
/// the honest "could not reach" for the ones that do not match any of them. Fast and deterministic because the
/// classification is exercised directly against crafted exceptions here; <see cref="NodeSessionsClientRealNetworkTests"/>
/// proves the connection-refused and certificate-mismatch wordings separately, over a real severed connection.
/// </summary>
public class NodeSessionsClientClassifyTests
{
    // Every failure shape the node can hand back, and the wording it reads as — with the node's own name asserted
    // for all of them, since a message that classified correctly and named nobody is no use with three nodes. Each
    // shape appears bare and wrapped: the SDK's transport wraps some one level deep and is free to add another.
    public static TheoryData<Exception, string> Failures() => new()
    {
        { new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused)), "looks stopped" },
        {
            new InvalidOperationException("wrapper", new HttpRequestException(
                "Connection refused", new SocketException((int)SocketError.ConnectionRefused))),
            "looks stopped"
        },
        // The exact shape `NodeCertificatePin.Require`'s validation callback produces on the wire (see
        // `NodePairingHandshakeTests.Claim_WithACertificateOtherThanThePinnedOne_IsRefusedWithThatReason`).
        { new HttpRequestException("TLS", new NodeCertificatePinMismatchException("AAAA", "BBBB")), "did not pin" },
        { new NodeCertificatePinMismatchException("AAAA", "BBBB"), "did not pin" },
        // What the call budget's own `CancellationTokenSource.CancelAfter` produces once it reaches `ReadAsync`'s
        // broad catch — the caller's own token was never touched, which is why this needs its own wording rather
        // than reading as "the caller gave up".
        { new OperationCanceledException(), "did not answer within" },
        { new HttpRequestException("initialize failed", new OperationCanceledException()), "did not answer within" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void EveryKnownFailureShape_ReadsAsItsOwnWording_AndNamesTheNode(Exception failure, string wording)
    {
        var message = NodeSessionsClient.Classify("laptop", failure);

        Assert.Contains("laptop", message, StringComparison.Ordinal);
        Assert.Contains(wording, message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureThatMatchesNoneOfTheKnownShapes_KeepsTheOldWording_RatherThanGuessing()
    {
        var message = NodeSessionsClient.Classify("laptop", new InvalidOperationException("something else entirely"));

        Assert.Equal("Could not reach laptop: something else entirely", message);
    }
}
