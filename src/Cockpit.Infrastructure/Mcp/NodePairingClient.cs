using System.Net;
using System.Net.Http.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-792: the controller's side of the handshake — three calls against a node's pairing port. BeginAsync runs
// with NodeCertificatePin.Observe (no pin yet, so it records the fingerprint the comparison code derives from);
// everything after runs pinned, so the machine that hands over the secret is the one whose code was compared.
internal sealed class NodePairingClient : INodePairingClient, ISingletonService
{
    // How often the controller asks whether the node's operator has answered. Short enough that Confirm feels
    // immediate at the other machine, long enough not to be a busy-loop over TLS for two minutes.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;

    public NodePairingClient()
        : this(TimeProvider.System)
    {
    }

    // Test seam: a controllable clock, so the poll loop can be driven without real waiting.
    internal NodePairingClient(TimeProvider time)
    {
        _time = time;
    }

    public async Task<NodePairingHandshake> BeginAsync(string address, string controllerName, CancellationToken cancellationToken = default)
    {
        var baseAddress = NormalizeAddress(address);

        string? seen = null;
        using var client = new HttpClient(NodeCertificatePin.Observe(fingerprint => seen = fingerprint));
        client.BaseAddress = baseAddress;

        using var response = await client.PostAsJsonAsync("pair/request", new NodePairingRequest(controllerName), NodePairingJson.Options, cancellationToken).ConfigureAwait(false);
        var offer = await _ReadOrThrowAsync<NodePairingOffer>(response, cancellationToken).ConfigureAwait(false);

        if (seen is null)
        {
            // Reachable only if the request somehow completed without a TLS handshake — a plain-HTTP address, say.
            // Refusing here beats continuing with a code derived from nothing.
            throw NodePairingException.For(
                NodePairingError.InvalidToken,
                "That address answered without a TLS certificate, so this cockpit has nothing to pin. A node's pairing address is always https.");
        }

        return new NodePairingHandshake(
            baseAddress.ToString(),
            offer.PairingId,
            offer.ClaimToken,
            offer.NodeName,
            NodePairingCode.Derive(offer.Nonce, seen),
            seen,
            offer.ExpiresAtUtc);
    }

    public async Task<NodePairingGrant> CompleteAsync(NodePairingHandshake handshake, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(NodeCertificatePin.Require(handshake.Fingerprint));
        client.BaseAddress = new Uri(handshake.Address);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var response = await client
                .PostAsJsonAsync("pair/claim", new NodePairingClaimRequest(handshake.PairingId, handshake.ClaimToken), NodePairingJson.Options, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                return await _ReadOrThrowAsync<NodePairingGrant>(response, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UnpairAsync(string address, string sharedSecret, string certificateFingerprint, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(NodeCertificatePin.Require(certificateFingerprint));
        client.BaseAddress = NormalizeAddress(address);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sharedSecret);

        using var response = await client.PostAsync("pair/unpair", content: null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await _ProblemAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    // The operator types an address, not a URL — the https scheme is filled in rather than demanded, and a
    // trailing slash added since HttpClient.BaseAddress silently drops the last path segment without one.
    public static Uri NormalizeAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var trimmed = address.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"https://{trimmed}";
        }

        if (!trimmed.EndsWith('/'))
        {
            trimmed += "/";
        }

        return new Uri(trimmed, UriKind.Absolute);
    }

    private static async Task<T> _ReadOrThrowAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await _ProblemAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync<T>(NodePairingJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw NodePairingException.For(NodePairingError.InvalidToken, "That address answered a pairing request with an empty body — it may not be a Cockpit node.");
    }

    // The node's own problem document if it sent one, falling back to the status only when the body isn't ours —
    // keeps "something else is listening on that port" from being reported as a pairing refusal.
    private static async Task<NodePairingException> _ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (await response.Content.ReadFromJsonAsync<NodePairingProblem>(NodePairingJson.Options, cancellationToken).ConfigureAwait(false) is { Error.Length: > 0 } problem)
            {
                return new NodePairingException(problem);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or HttpRequestException or NotSupportedException)
        {
            // Not a Cockpit node, or not answering like one. Handled by the fallback below.
        }

        return NodePairingException.For(
            NodePairingError.InvalidToken,
            $"That address answered a pairing request with HTTP {(int)response.StatusCode} and no reason — it may not be a Cockpit node.");
    }
}
