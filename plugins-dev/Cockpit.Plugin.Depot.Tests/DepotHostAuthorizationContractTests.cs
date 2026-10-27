using System.Net;
using System.Net.Http.Json;
using Cockpit.Plugin.Depot.Backup;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.FirstStart;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// Layer 2: the account endpoints must acquire their host-only scope instead of reusing the session token.
public sealed class DepotHostAuthorizationContractTests
{
    [Fact]
    public async Task Account_endpoint_callers_request_the_depot_host_scope()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CreateAuthorizedHandler(Arg.Any<string>(), "depot.host").Returns(_ => new _DepotAccountHandler());
        host.CreateAuthorizedHandler(Arg.Any<string>()).Returns(_ => new _DepotAccountHandler());
        var connection = new DepotConnectionRegistration("c1", "Home", "https://depot.test", SynchroniseSettings: true);
        var settings = new DepotSettings(new FakePluginStorage()) { Connections = [connection] };

        var target = new DepotBackupTarget(host, () => settings.Connections, connection.Id);
        await target.ListAsync();

        var carrier = new DepotSettingsCarrier(host, () => settings.Connections, connection.Id);
        Assert.Null(await carrier.ReadAsync(CancellationToken.None));

        var firstStart = new DepotFirstStart(host, settings, _ => { }, () => host, () => null);
        var manifest = await firstStart.ReadManifestAsync(
            new DepotFirstStartManifestRequest(target.Id, "acme"),
            CancellationToken.None);
        Assert.Null(manifest.Manifest);

        host.Received(2).CreateAuthorizedHandler(connection.McpServerName, "depot.host");
        host.Received(1).CreateAuthorizedHandler(connection.McpServerName);
    }

    private sealed class _DepotAccountHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/cockpit/backups" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<object>()),
                },
                "/cockpit/profile" => new HttpResponseMessage(HttpStatusCode.NoContent),
                "/cockpit/manifest" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
    }
}
