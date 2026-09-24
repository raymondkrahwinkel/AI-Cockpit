using System.Text;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

internal static class FilesEndpoint
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        api.MapGet("/projects/{id}/file", (Func<HttpContext, Task<IResult>>)(context =>
            ReadAsync(context, context.Request.RouteValues["id"]?.ToString() ?? string.Empty, services))).RequireOperate();
    }

    internal static async Task<IResult> ReadAsync(HttpContext context, string id, IServiceProvider services)
    {
        var caller = McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A file route ran without a connect-key caller.");
        var project = await services.GetRequiredService<IProjectEditor>().FindProjectAsync(id).ConfigureAwait(false);
        if (project?.SourceDirectory is not { } root ||
            !caller.AllowsProject(id, services.GetRequiredService<INodePairingBroker>()))
        {
            return Results.NotFound();
        }

        if (!ProjectRootPath.TryResolve(root, context.Request.Query["path"].ToString(), out var fullPath, out var refusal))
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_path", refusal ?? "Invalid path.");
        }

        if (Directory.Exists(fullPath))
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_path", "The path names a directory.");
        }

        try
        {
            await using var file = File.OpenRead(fullPath);
            if (file.Length > MaxBytes)
            {
                return BackendApiRoutes.Error(StatusCodes.Status413PayloadTooLarge, "file_too_large", "The file exceeds 1 MiB.");
            }

            var buffer = new byte[MaxBytes + 1];
            var count = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, context.RequestAborted).ConfigureAwait(false);
            if (count > MaxBytes)
            {
                return BackendApiRoutes.Error(StatusCodes.Status413PayloadTooLarge, "file_too_large", "The file exceeds 1 MiB.");
            }

            var bytes = buffer[..count];
            try
            {
                var text = StrictUtf8.GetString(bytes);
                if (!text.Contains('\0'))
                {
                    return Results.Bytes(bytes, "text/plain; charset=utf-8");
                }
            }
            catch (DecoderFallbackException)
            {
            }

            return Results.Bytes(bytes, "application/octet-stream");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
    }
}
