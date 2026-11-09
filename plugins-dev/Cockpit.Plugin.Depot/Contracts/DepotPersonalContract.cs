namespace Cockpit.Plugin.Depot.Contracts;

// What the UI part and the backend part say to each other about personal MCPs; compiled into both assemblies.
internal static class DepotPersonalChannel
{
    // Payload DepotPersonalListRequest, answers a DepotPersonalList; Refresh reads the discovery first.
    public const string List = "personal-list";

    // Payload DepotPersonalTarget, answers the address to open in the browser; one wait at a time.
    public const string Authorize = "personal-authorize";

    // Payload ignored; stops the wait.
    public const string Cancel = "personal-cancel";

    // Event, payload ignored: the list or the wait changed.
    public const string Changed = "personal-changed";

    // Event, payload DepotPersonalTarget: the bell's Authorize, which the UI part runs like the row's button.
    public const string AuthorizeRequested = "personal-authorize-requested";
}

// The statuses of Depot's gateway contract (`Docs/mcp-gateway-cockpit-contract.md`, fields section).
internal static class DepotPersonalStatus
{
    public const string NotAuthorized = "NotAuthorized";

    public const string Active = "Active";

    public const string Expired = "Expired";

    public const string Unreachable = "Unreachable";

    // Unreachable has nothing to authorize, because that authorization already exists.
    public static bool NeedsAuthorization(string status) => status is NotAuthorized or Expired;
}

internal sealed record DepotPersonalMcp(string Id, string Name, string Status, string AuthorizeUrl);

internal sealed record DepotPersonalListRequest(bool Refresh);

internal sealed record DepotPersonalConnection(string ConnectionId, string ConnectionName, IReadOnlyList<DepotPersonalMcp> Mcps);

// `WaitingId` is the MCP whose browser is waited for, null when none; every other button is off then.
internal sealed record DepotPersonalList(IReadOnlyList<DepotPersonalConnection> Connections, string? WaitingId);

internal sealed record DepotPersonalTarget(string ConnectionId, string Id);

internal sealed record DepotPersonalAuthorizeAnswer(string? Url, string? Message = null);
