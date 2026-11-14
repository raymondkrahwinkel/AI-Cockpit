using System.Net;
using System.Text.Json;

namespace Cockpit.Plugin.Matomo;

// The only way this plugin reaches Matomo: a POST of `module=API&method=…` to {url}/index.php, with the token in
// the body. Only the methods below can be called, so no Matomo write method is reachable.
internal sealed class MatomoClient(HttpClient http)
{
    internal static readonly IReadOnlySet<string> AllowedMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "API.getMatomoVersion",
        "SitesManager.getSitesWithAtLeastViewAccess",
        "API.getReportMetadata",
        "API.getProcessedReport",
        "Live.getCounters",
    };

    internal static readonly IReadOnlyDictionary<string, string> NoArgs = new Dictionary<string, string>();

    private static readonly HashSet<string> Reserved = new(["module", "method", "format", "token_auth"], StringComparer.OrdinalIgnoreCase);

    public async Task<JsonElement> CallAsync(string url, string token, string method, IReadOnlyDictionary<string, string> args, bool siteBound, CancellationToken cancellationToken)
    {
        if (!AllowedMethods.Contains(method) || args.Keys.Any(Reserved.Contains))
        {
            throw new InvalidOperationException($"Matomo method {method} is not allowed.");
        }

        if (!Uri.TryCreate(url.TrimEnd('/') + "/index.php", UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            throw new MatomoException("not_configured", "The Matomo URL is not a valid http(s) address.", "Enter the address of your Matomo server, for example https://matomo.example.com.");
        }

        var form = new Dictionary<string, string>(args) { ["module"] = "API", ["method"] = method, ["format"] = "JSON", ["token_auth"] = token };
        string body;
        HttpStatusCode status;
        try
        {
            using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new MatomoException("server_unreachable", $"Matomo at {endpoint.Host} did not answer ({ex.GetType().Name}).", "Check the URL, that the server is running and reachable from this machine.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? _TokenRejected()
                : new MatomoException("matomo_error", status == HttpStatusCode.OK ? "Matomo did not answer with JSON." : $"Matomo answered HTTP {(int)status}.", "Check that the URL points at the Matomo root, not at a login page or a proxy.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result) && result.GetString() == "error")
            {
                var message = root.TryGetProperty("message", out var text) ? text.GetString() ?? string.Empty : string.Empty;
                throw _FromMatomoError(message.Replace(token, "***", StringComparison.Ordinal), siteBound);
            }

            return (int)status is >= 200 and < 300
                ? root.Clone()
                : throw new MatomoException("matomo_error", $"Matomo answered HTTP {(int)status}.", "Check that the URL points at the Matomo root and that the server is healthy.");
        }
    }

    private static MatomoException _TokenRejected() =>
        new("token_rejected", "Matomo rejected the token.", "Create a new token for a user with view access in Matomo (Personal › Security) and enter it in the plugin settings.");

    private static MatomoException _FromMatomoError(string message, bool siteBound)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("token_auth") || lower.Contains("authenticat"))
        {
            return _TokenRejected();
        }

        if (lower.Contains("access"))
        {
            return siteBound
                ? new MatomoException("no_access", "The token has no view access to this site.", "Give the Matomo user view access to the site, or offer another site.")
                : _TokenRejected();
        }

        if (lower.Contains("report") && (lower.Contains("not found") || lower.Contains("not exist") || lower.Contains("not available")))
        {
            return new MatomoException("unknown_field", "Matomo does not know that report.", "Call analytics_list_fields for the site and use one of its fields.");
        }

        return new MatomoException("matomo_error", $"Matomo reported: {message}", "Check the arguments, such as the dates (YYYY-MM-DD, today, yesterday, NdaysAgo).");
    }
}
