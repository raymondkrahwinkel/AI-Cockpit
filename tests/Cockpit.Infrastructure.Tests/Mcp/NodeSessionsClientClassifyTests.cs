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
    [Fact]
    public void ARefusedSocketException_ReadsAsTheNodeLookingStopped()
    {
        var exception = new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

        var message = NodeSessionsClient.Classify("laptop", exception);

        Assert.Contains("laptop", message, StringComparison.Ordinal);
        Assert.Contains("refused", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("looks stopped", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedSocketException_NestedTwoLevelsDeep_IsStillFound()
    {
        // The shape the SDK's own transport actually produces is one level of wrapping; this pins that a third
        // layer added upstream would not silently fall through to the unclassified wording.
        var exception = new InvalidOperationException("wrapper", new HttpRequestException(
            "Connection refused", new SocketException((int)SocketError.ConnectionRefused)));

        var message = NodeSessionsClient.Classify("laptop", exception);

        Assert.Contains("looks stopped", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACertificatePinMismatch_WrappedInHttpRequestException_NamesTheUntrustedCertificate()
    {
        // The exact shape `NodeCertificatePin.Require`'s validation callback produces on the wire (see
        // `NodePairingHandshakeTests.Claim_WithACertificateOtherThanThePinnedOne_IsRefusedWithThatReason`).
        var exception = new HttpRequestException("TLS", new NodeCertificatePinMismatchException("AAAA", "BBBB"));

        var message = NodeSessionsClient.Classify("laptop", exception);

        Assert.Contains("did not pin", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACertificatePinMismatch_AtTheTopLevel_IsStillClassified()
    {
        var message = NodeSessionsClient.Classify("laptop", new NodeCertificatePinMismatchException("AAAA", "BBBB"));

        Assert.Contains("did not pin", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACancellationTheCallerNeverRequested_ReadsAsATimeout()
    {
        // This is what the call budget's own `CancellationTokenSource.CancelAfter` produces once it reaches
        // `ReadAsync`'s broad catch — the caller's own token was never touched, which is exactly why this shape
        // needs its own wording rather than reading as "the caller gave up".
        var message = NodeSessionsClient.Classify("laptop", new OperationCanceledException());

        Assert.Contains("laptop", message, StringComparison.Ordinal);
        Assert.Contains("did not answer within", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACancellationWrappedByTheTransport_IsStillReadAsATimeout()
    {
        // `McpClient.CreateAsync`'s own `InitializationTimeout`/`DiscoverProbeTimeout` are as free to wrap the
        // cancellation as the SDK's call path is — this must not fall through to the unclassified case just
        // because the timeout did not arrive bare.
        var exception = new HttpRequestException("initialize failed", new OperationCanceledException());

        var message = NodeSessionsClient.Classify("laptop", exception);

        Assert.Contains("did not answer within", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureThatMatchesNoneOfTheKnownShapes_KeepsTheOldWording_RatherThanGuessing()
    {
        var message = NodeSessionsClient.Classify("laptop", new InvalidOperationException("something else entirely"));

        Assert.Equal("Could not reach laptop: something else entirely", message);
    }
}
