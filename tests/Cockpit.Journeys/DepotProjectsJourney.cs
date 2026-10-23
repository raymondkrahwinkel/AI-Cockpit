using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Journeys;

// AC-1634: the bundled Depot plugin's own project discovery is REST-only on a current Depot. The fake deliberately
// has no working MCP endpoint, so either picker regresses visibly when a plugin-owned list_projects call returns.
[Collection(JourneyCollection.Alone)]
public sealed class DepotProjectsJourney
{
    [Fact]
    public async Task ADepotWithoutTheListProjectsTool_StillFillsTheMemoryPickerAndSharedProjectList()
    {
        await using var depot = await _FakeDepot.StartAsync();
        await using var cockpit = JourneyHost.Desktop();
        var url = depot.BaseUrl.TrimEnd('/');
        var connections = JsonSerializer.Serialize(new[] { new { Id = "journey", Name = "Journey", Url = url } });
        await cockpit.Services.GetRequiredService<IPluginRegistrationStore>().SaveDataAsync(
            "depot", new Dictionary<string, string> { ["connections"] = connections });
        await cockpit.Services.GetRequiredService<IMcpOAuthTokenStore>().SaveAsync(
            "journey",
            "Depot: Journey",
            new McpOAuthToken
            {
                AccessToken = Bearer,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Scope = "depot",
                ResourceUrl = $"{url}/mcp",
            });
        await cockpit.StartDesktopAsync();

        var memory = cockpit.Services.GetRequiredService<IProjectMemorySourceRegistry>().Sources.Single(source => source.Scheme == "depot");
        var shared = cockpit.Services.GetRequiredService<ISharedProjectSourceRegistry>().Sources.Single(source => source.Key == "depot");
        var locations = await memory.ListLocationsAsync!(CancellationToken.None);
        var projects = await shared.ListAsync(CancellationToken.None);

        Assert.True(locations.Outcome == ProjectMemorySourceLocationsOutcome.Success, locations.Error);
        Assert.Equal("acme/cockpit", Assert.Single(locations.Locations).Value);
        Assert.Equal("Cockpit from Depot", Assert.Single(projects.Projects).Name);
    }

    private const string Bearer = "journey-depot-projects-bearer";

    private sealed class _NoListProjectsTools
    {
        [McpServerTool(Name = "ping"), Description("A harmless tool; list_projects is deliberately absent.")]
        public static string Ping() => "pong";
    }

    private sealed class _FakeDepot(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl { get; } = baseUrl;

        public static async Task<_FakeDepot> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddMcpServer().WithHttpTransport().WithTools<_NoListProjectsTools>();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/cockpit") || context.Request.Path.StartsWithSegments("/mcp"))
                {
                    if (context.Request.Headers.Authorization != $"Bearer {Bearer}")
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                await next();
            });
            app.MapGet("/cockpit/projects", _WriteProjectsAsync);
            app.MapMcp("/mcp");
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("The fake Depot did not expose its loopback address.");
            return new _FakeDepot(app, addresses.Addresses.Single().TrimEnd('/'));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private static async Task _WriteProjectsAsync(HttpContext context)
        {
            context.Response.Headers.ETag = "\"journey-projects-v1\"";
            await context.Response.WriteAsJsonAsync(new
            {
                projects = new[]
                {
                    new
                    {
                        address = "acme/cockpit",
                        slug = "cockpit",
                        name = "Cockpit",
                        role = "Editor",
                        kind = "Project",
                        organization = new { slug = "acme", name = "Acme", role = "Member" },
                        cockpit = new
                        {
                            state = "ok",
                            checksum = "definition-v1",
                            updatedAt = DateTimeOffset.UtcNow,
                            definition = new { schemaVersion = 1, name = "Cockpit from Depot" },
                        },
                    },
                },
            });
        }
    }
}
