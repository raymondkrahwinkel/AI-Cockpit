using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Matomo.Contracts;
using Cockpit.Plugin.Matomo.Mcp;
using Cockpit.Plugin.Matomo.Settings;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Matomo;

// Read-only Matomo reports for agents (AC-2330): the cockpit-matomo MCP server plus the answer to the settings
// view's Test connection. The token stays in this part; the UI part only ever sends what the operator typed.
public sealed class MatomoPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "matomo",
        DisplayName: "Matomo",
        Author: "Cockpit",
        Description: "Give agents read-only access to the reports of your own Matomo server through a cockpit-matomo MCP server. The plugin calls the Matomo Reporting API itself with a view-only token and keeps that token private. You choose which sites agents may read.");

    private readonly List<IDisposable> _handlers = [];
    private HttpClient? _http;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new MatomoSettings(host.Storage);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new MatomoClient(_http);

        _ = host.AddMcpEndpoint("cockpit-matomo", new MatomoMcpTools(settings, client), isEnabled: () => settings.McpEnabled);

        _handlers.Add(host.Channel.Handle(MatomoChannel.TestConnection, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<MatomoTestRequest>(MatomoChannel.Json) ?? new MatomoTestRequest(string.Empty, null);
            var answer = await _TestAsync(client, request.Url.Trim(), string.IsNullOrWhiteSpace(request.Token) ? settings.AuthToken : request.Token, cancellationToken);
            return JsonSerializer.SerializeToElement(answer, MatomoChannel.Json);
        }));
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
        _http?.Dispose();
    }

    private static async Task<MatomoTestAnswer> _TestAsync(MatomoClient client, string url, string token, CancellationToken cancellationToken)
    {
        if (url.Length == 0 || token.Length == 0)
        {
            return new MatomoTestAnswer(MatomoStatus.NotConfigured, "Enter the Matomo URL and a token first.", null, []);
        }

        try
        {
            var version = await client.CallAsync(url, token, "API.getMatomoVersion", MatomoClient.NoArgs, siteBound: false, cancellationToken);
            var sites = await client.CallAsync(url, token, "SitesManager.getSitesWithAtLeastViewAccess", MatomoClient.NoArgs, siteBound: false, cancellationToken);
            var list = sites.EnumerateArray()
                .Select(site => new MatomoSite(int.Parse(site.GetProperty("idsite").ToString(), System.Globalization.CultureInfo.InvariantCulture), site.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty))
                .ToList();
            var versionText = version.TryGetProperty("value", out var value) ? value.GetString() : null;
            return list.Count == 0
                ? new MatomoTestAnswer(MatomoStatus.NoSites, "Connected, but this token can view no sites. Give its user view access to a site in Matomo.", versionText, list)
                : new MatomoTestAnswer(MatomoStatus.Connected, $"Connected to Matomo {versionText} with {list.Count} site(s).", versionText, list);
        }
        catch (MatomoException ex)
        {
            var status = ex.Code == "token_rejected" ? MatomoStatus.TokenRejected : MatomoStatus.Unreachable;
            return new MatomoTestAnswer(status, $"{ex.Message} {ex.Fix}", null, []);
        }
    }
}
