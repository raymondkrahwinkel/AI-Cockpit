using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.TestSupport;

namespace Cockpit.Journeys;

// AC-1634: the bundled Depot plugin's own project discovery is REST-only on a current Depot. The fake deliberately
// has no working MCP endpoint, so either picker regresses visibly when a plugin-owned list_projects call returns.
[Collection(JourneyCollection.Alone)]
public sealed class DepotProjectsJourney
{
    [Fact]
    public async Task ADepotWithoutTheListProjectsTool_StillFillsTheMemoryPickerAndSharedProjectList()
    {
        await using var depot = await LoopbackHttpServer.StartAsync(_HandleAsync);
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

    private static async Task _HandleAsync(HttpContext context)
    {
        if (context.Request.Headers.Authorization != $"Bearer {Bearer}")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (context.Request.Path == "/cockpit/projects")
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
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync("tool not found");
    }
}
