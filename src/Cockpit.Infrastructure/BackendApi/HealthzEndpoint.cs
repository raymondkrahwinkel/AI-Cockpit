using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1466: Docker's health probe, on the node listener and without a key — McpAuthMiddleware lets exactly this path
// through. It says only whether each section holds: no rows, profiles, version, keys or paths. Outside /api/v1, so
// the API's own door never sees it.
internal static class HealthzEndpoint
{
    public const string Path = "/healthz";

    public static void Map(WebApplication app, IServiceProvider services) =>
        app.MapGet(Path, () =>
        {
            var sections = services.GetRequiredService<PluginHealthSections>().Read();
            var healthy = sections.All(section => section.Report.Healthy);
            return Results.Json(
                new
                {
                    status = healthy ? "healthy" : "unhealthy",
                    sections = sections.Select(section => new { name = section.Name, healthy = section.Report.Healthy }),
                },
                statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });
}
