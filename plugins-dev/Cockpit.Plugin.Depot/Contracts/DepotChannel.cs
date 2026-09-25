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

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

// A connection's wire shape for the channel only — deliberately not the backend's own Model.DepotConnectionRegistration
// (or a linked copy of it): that record is constructed directly by name across most of this plugin's own test
// suite, on both what will become the backend and the UI side, and a copy of it under the same name in both
// assemblies would be ambiguous to Cockpit.Plugin.Depot.Tests, which sees both assemblies' internals.
internal sealed record DepotConnectionPayload(string Id, string Name, string Url);

internal sealed record DepotSaveConnectionsRequest(IReadOnlyList<DepotConnectionPayload> Connections);

internal sealed record DepotSaveConnectionsAnswer(bool Success);
