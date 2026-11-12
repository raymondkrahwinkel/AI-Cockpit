using System.Text.Json;

namespace Cockpit.Plugin.Matomo.Contracts;

// Linked into both assemblies, so the UI part never references the backend part.
internal static class MatomoChannel
{
    // Payload: MatomoTestRequest. Answers a MatomoTestAnswer, made where the backend runs.
    public const string TestConnection = "test-connection";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal static class MatomoKeys
{
    public const string Url = "url";
    public const string AuthToken = "authToken";
    public const string McpEnabled = "mcpEnabled";
    public const string OfferedSites = "offeredSites";
    public const string DefaultSite = "defaultSiteId";
}

internal static class MatomoStatus
{
    public const string Connected = "connected";
    public const string TokenRejected = "token_rejected";
    public const string Unreachable = "unreachable";
    public const string NoSites = "no_sites";
    public const string NotConfigured = "not_configured";
}

internal sealed record MatomoSite(int Id, string Name);

// A blank Token means the stored one.
internal sealed record MatomoTestRequest(string Url, string? Token);

internal sealed record MatomoTestAnswer(string Status, string Message, string? Version, IReadOnlyList<MatomoSite> Sites);
