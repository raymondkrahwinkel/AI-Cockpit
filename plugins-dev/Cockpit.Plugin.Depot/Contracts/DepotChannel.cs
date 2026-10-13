using System.Text.Json;

namespace Cockpit.Plugin.Depot.Contracts;

// AC-1394: what the UI part asks the backend part over the plugin's channel. Compiled into both assemblies as a
// linked source file rather than shared as an assembly, so the UI part never references the backend part.
internal static class DepotChannel
{
    // Payload: DepotSaveConnectionsRequest. Persists the connection list on the backend part — the write
    // Ui.DepotSettingsControl.Save used to do directly against ICockpitHost before AC-1394 split this plugin:
    // syncing the memory-source and shared-project-source registries against the connections currently in
    // storage, then reclaiming any orphaned MCP-registry entry. Answers a DepotSaveConnectionsAnswer.
    public const string SaveConnections = "save-connections";

    // Event, payload ignored: the backend part asks the UI part to open this plugin's settings.
    public const string OpenSettings = "open-settings";

    // AC-1511. Payload: DepotInstallTokenRequest. Adds the connection, or updates the one with its id or URL, through
    // the same write SaveConnections does, then signs it in by redeeming the installation token at Depot — no
    // browser, which a server does not have. A token Depot refuses changes nothing. Answers a DepotInstallTokenAnswer.
    public const string ConnectWithInstallToken = "connect-with-install-token";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

// A connection's wire shape for the channel only — deliberately not the backend's own Model.DepotConnectionRegistration
// (or a linked copy of it): that record is constructed directly by name across most of this plugin's own test
// suite, on both what will become the backend and the UI side, and a copy of it under the same name in both
// assemblies would be ambiguous to Cockpit.Plugin.Depot.Tests, which sees both assemblies' internals.
internal sealed record DepotConnectionPayload(string Id, string Name, string Url);

internal sealed record DepotSaveConnectionsRequest(IReadOnlyList<DepotConnectionPayload> Connections);

internal sealed record DepotSaveConnectionsAnswer(bool Success);

// The token is a credential: the generated ToString would print it into any log line or exception that names the
// request, so it is masked here.
internal sealed record DepotInstallTokenRequest(DepotConnectionPayload Connection, string Token)
{
    public override string ToString() => $"{nameof(DepotInstallTokenRequest)} {{ Connection = {Connection}, Token = *** }}";
}

// `Message` is the sentence for the operator, the same in the desktop's row and the server's view.
internal sealed record DepotInstallTokenAnswer(bool Success, string Message);
