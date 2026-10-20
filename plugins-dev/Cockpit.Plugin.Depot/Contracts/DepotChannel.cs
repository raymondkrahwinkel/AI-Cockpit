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

    // AC-1509, the first start's route C. Payload: DepotFirstStartSignInRequest. Signs in to the Depot at that address through
    // the host's own sign-in and writes the connection only once that succeeded, so a skip or an offline machine leaves
    // none behind. Then reads what the account holds for the two choices. Answers a DepotFirstStartAnswer.
    public const string FirstStartSignIn = "first-start-sign-in";

    public const string FirstStartManifest = "first-start-manifest";

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

internal sealed record DepotFirstStartSignInRequest(string Url);

// `State` is one of the DepotFirstStartState values; `Message` the sentence for the operator. Signed in: `TargetId` names
// the account space as a backup target, `Backups` lists it newest first, and `Manifest` is the account's
// /cockpit/manifest document — null when the server has none (a 404) or it could not be read, which `Notes` then says.
internal sealed record DepotFirstStartAnswer(
    string State,
    string Message,
    string? TargetId = null,
    IReadOnlyList<DepotFirstStartBackup>? Backups = null,
    string? Manifest = null,
    IReadOnlyList<string>? Notes = null,
    IReadOnlyList<DepotFirstStartCandidate>? Candidates = null);

internal sealed record DepotFirstStartBackup(string Id, DateTimeOffset CreatedAt, long Size);

internal sealed record DepotFirstStartCandidate(string Slug, string Name);

internal sealed record DepotFirstStartManifestRequest(string TargetId, string Organization);

internal sealed record DepotFirstStartManifestAnswer(string? Manifest, IReadOnlyList<string>? Notes = null);

internal static class DepotFirstStartState
{
    public const string SignedIn = "signed-in";

    public const string Offline = "offline";

    public const string NotSignedIn = "not-signed-in";

    public const string Invalid = "invalid";
}
