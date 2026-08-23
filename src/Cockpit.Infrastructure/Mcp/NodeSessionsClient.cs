using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Mcp;

// AC-795: the controller's side — a short-lived MCP client per call over the pinned HTTPS transport, not a
// held-open connection, since a node sleeps/moves/switches off. ponytail: no connection reuse or retry even
// with the AC-796 timer poll — cheap enough at 20s that pooling isn't worth it yet, key on node name if it is.
internal sealed class NodeSessionsClient(
    IMcpServerStore servers,
    ILogger<NodeSessionsClient> logger) : INodeSessionsClient, ISingletonService
{
    // A node on a local network answers in milliseconds or it is not there. Long enough to survive a busy machine,
    // short enough that a button does not appear to hang.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        var known = await servers.LoadAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. known
                .Select(server => NodeServerName.Split(server.Name))
                .Where(split => split is { } parts
                    && string.Equals(parts.ServerName, NodeServerName.SessionsServerName, StringComparison.Ordinal))
                .Select(split => split!.Value.NodeName)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    public async Task<NodeSessionsSnapshot> ReadAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        try
        {
            // One connection, three calls: the three lists are read together because they are shown together, and
            // a snapshot assembled from three separate handshakes would be three chances for the node to go away
            // mid-refresh and leave a half-drawn card.
            await using var client = await _ConnectAsync(nodeName, cancellationToken).ConfigureAwait(false);

            var sessions = await _CallAsync(client, "list_node_sessions", null, cancellationToken).ConfigureAwait(false);
            var profiles = await _CallAsync(client, "list_node_profiles", null, cancellationToken).ConfigureAwait(false);
            var projects = await _CallAsync(client, "list_node_projects", null, cancellationToken).ConfigureAwait(false);

            if ((_ErrorIn(sessions) ?? _ErrorIn(profiles) ?? _ErrorIn(projects)) is { } refusal)
            {
                return new NodeSessionsSnapshot(nodeName, [], [], [], refusal);
            }

            return new NodeSessionsSnapshot(
                nodeName,
                [.. _Array(sessions, "sessions").Select(row => new NodeSessionRow(
                    _Text(row, "paneId"),
                    _Text(row, "name"),
                    _Text(row, "profile"),
                    _Text(row, "statusline")))],
                [.. _Array(profiles, "profiles").Select(row => new NodeScopedProfileSummary(
                    _Text(row, "label"),
                    // An unknown provider name is not a reason to drop a profile the operator is allowed to run:
                    // this side may be the older build of the two, and the label is what the grant is keyed on.
                    Enum.TryParse<SessionProvider>(_Text(row, "provider"), out var provider) ? provider : default,
                    _Text(row, "purpose") is { Length: > 0 } purpose ? purpose : null))],
                [.. _Array(projects, "projects").Select(row => new NodeProjectRow(_Text(row, "id"), _Text(row, "name")))]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Iron Law #8's line, and the same one `McpToolProbe` draws: a node name is configuration, the shared
            // secret behind the connection is not, and nothing from the payload is logged.
            logger.LogInformation(exception, "Could not read the sessions on node {Node}.", nodeName);
            return new NodeSessionsSnapshot(nodeName, [], [], [], Classify(nodeName, exception));
        }
    }

    public Task<string?> StartAsync(
        string nodeName,
        string profileLabel,
        string? projectId = null,
        string? prompt = null,
        string? sessionName = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["profile"] = profileLabel };
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            arguments["projectId"] = projectId;
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            arguments["prompt"] = prompt;
        }

        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            arguments["name"] = sessionName;
        }

        return _ActAsync(nodeName, "start_node_agent", arguments, cancellationToken);
    }

    public Task<string?> StopAsync(string nodeName, string paneId, CancellationToken cancellationToken = default) =>
        _ActAsync(nodeName, "stop_node_agent", new Dictionary<string, object?> { ["paneId"] = paneId }, cancellationToken);

    // Null when the node did it, its own words when it refused, a sentence of ours when it could not be reached.
    // The three are deliberately one return value: to the operator pressing the button they are the same question —
    // did this happen — and only the text differs.
    private async Task<string?> _ActAsync(
        string nodeName,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var client = await _ConnectAsync(nodeName, cancellationToken).ConfigureAwait(false);
            var result = await _CallAsync(client, toolName, arguments, cancellationToken).ConfigureAwait(false);
            return _ErrorIn(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogInformation(exception, "Could not reach node {Node} to call {Tool}.", nodeName, toolName);
            return Classify(nodeName, exception);
        }
    }

    private async Task<McpClient> _ConnectAsync(string nodeName, CancellationToken cancellationToken)
    {
        var known = await servers.LoadAsync(cancellationToken).ConfigureAwait(false);
        var wanted = NodeServerName.For(nodeName, NodeServerName.SessionsServerName);
        var server = known.FirstOrDefault(candidate => string.Equals(candidate.Name, wanted, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"This cockpit is not paired with a node called '{nodeName}', or that pairing predates session management. Pair with it again from Options → Security.");

        // The same pin the session route applies to this row (AC-792) — a screen that trusted more than a session
        // does would show a node as reachable that nothing else here can reach. The bearer is the node's own shared
        // secret off the row, which is what stamps this caller as the controller on the far side (AC-791).
        var transport = NodeCertificatePin.TransportFor(server, new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url ?? string.Empty),
            TransportMode = HttpTransportMode.AutoDetect,
            AdditionalHeaders = CockpitMcpBearer.UserApiKey(server) is { } bearer
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Bearer {bearer}" }
                : null,
        });

        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions { InitializationTimeout = Budget, DiscoverProbeTimeout = Budget },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> _CallAsync(
        McpClient client,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: budget.Token).ConfigureAwait(false);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        // Every tool on `NodeSessionMcpTools` answers with a JSON object, including its refusals — a protocol-level
        // error would mean the far side is not the server this expects, and that is not something to paper over
        // with an empty list.
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // A node's own refusal, verbatim, or null when the call went through. Shown as-is: the node wrote it for the
    // operator ("this node's operator has not allowed the profile X"), and rephrasing it here would lose the one
    // detail that says what to go and tick.
    private static string? _ErrorIn(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("ok", out var ok)
            && ok.ValueKind == JsonValueKind.False
                ? payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : "The node refused, without saying why."
                : null;

    private static IEnumerable<JsonElement> _Array(JsonElement payload, string property) =>
        payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(property, out var array)
            && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray()
                : [];

    private static string _Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    // AC-796 criterion 2: distinct wording for a refused connection, an untrusted certificate and a timeout,
    // classified by unwrapping the real exception shape (SocketException, NodeCertificatePin.Require's wrapper,
    // OperationCanceledException) rather than parsing exception.Message, which isn't a stable contract.
    internal static string Classify(string nodeName, Exception exception)
    {
        if (_Find<NodeCertificatePinMismatchException>(exception) is not null)
        {
            return $"{nodeName} answered with a certificate this cockpit did not pin. That is not the machine you "
                + "paired with, or it was reinstalled — pair again from Options → Security if that is expected.";
        }

        if (_Find<SocketException>(exception) is { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return $"{nodeName} refused the connection — nothing is listening there. It looks stopped, not merely out of reach.";
        }

        if (_Find<OperationCanceledException>(exception) is not null)
        {
            return $"{nodeName} did not answer within {Budget.TotalSeconds:0}s. The connection may be down, or the "
                + "node may simply be asleep or busy — there is no way to tell which from here.";
        }

        // Real, but not one of the shapes above — the honest "could not reach" rather than picking the
        // closest-sounding category.
        return $"Could not reach {nodeName}: {exception.Message}";
    }

    private static T? _Find<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
