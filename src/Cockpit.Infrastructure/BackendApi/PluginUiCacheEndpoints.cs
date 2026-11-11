using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

internal static class PluginUiCacheEndpoints
{
    internal static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        api.MapGet("/plugins/{folderId}/ui/package", async (string folderId, string version, string sha256, CancellationToken cancellationToken) =>
        {
            var loaded = services.GetService<PluginManager>()?.Loaded.FirstOrDefault(plugin => plugin.FolderId == folderId);
            var installed = (await services.GetRequiredService<IPluginAdministration>().GetInstalledAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(plugin => plugin.Discovered.FolderId == folderId);
            if (loaded is null || loaded.Manifest.UiAssembly is null || installed?.Registration is not { Enabled: true } registration)
            {
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin_ui", "That plugin has no enabled UI on the server.");
            }
            if (!string.Equals(loaded.Manifest.Version, version, StringComparison.Ordinal)
                || !string.Equals(loaded.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "plugin_changed", "The running plugin changed. Refresh its inventory before loading its UI.");
            }
            try
            {
                // The running closure remains approved while a newer installed version waits for a restart.
                var shadow = PluginShadow.GetOrPrepare(loaded);
                var files = new List<PluginUiPackageFile>();
                long length = 0;
                foreach (var path in _Files(shadow.Folder))
                {
                    var size = new FileInfo(path).Length;
                    if (files.Count >= PluginUiPackage.MaxFiles || size > PluginUiPackage.MaxBytes - length)
                    {
                        return BackendApiRoutes.Error(StatusCodes.Status413PayloadTooLarge, "ui_too_large", "The plugin UI package exceeds its transfer limits.");
                    }
                    shadow.Verify(path);
                    var bytes = new byte[checked((int)size)];
                    await using (var stream = File.OpenRead(path))
                    {
                        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    }
                    length += bytes.LongLength;
                    if (length > PluginUiPackage.MaxBytes)
                    {
                        return BackendApiRoutes.Error(StatusCodes.Status413PayloadTooLarge, "ui_too_large", "The plugin UI package exceeds its transfer limits.");
                    }
                    files.Add(new PluginUiPackageFile(Path.GetRelativePath(shadow.Folder, path).Replace('\\', '/'), bytes));
                }
                var hash = PluginHash.ComputeClosure(files.Select(file => new PluginClosureFile(file.Path, PluginHash.Compute(file.Bytes))));
                if (!string.Equals(hash, loaded.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PluginIntegrityException("The plugin closure changed during transfer.");
                }
                var current = services.GetService<PluginManager>()?.Loaded.FirstOrDefault(plugin => plugin.FolderId == folderId);
                var registrations = await services.GetRequiredService<IPluginRegistrationStore>().LoadAllAsync(cancellationToken).ConfigureAwait(false);
                if (current?.Sha256 != loaded.Sha256 || registrations.GetValueOrDefault(folderId) is not { Enabled: true } latest
                    || !string.Equals(latest.PinnedSha256, registration.PinnedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "plugin_changed", "The running plugin changed. Refresh its inventory before loading its UI.");
                }
                return Results.Json(new PluginUiPackage(folderId, loaded.Manifest, hash, files), ConnectKeyEndpoints.Json);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PluginIntegrityException)
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "plugin_changed", "The approved plugin UI files are unavailable or changed.");
            }
        }).RequireAdmin();
    }

    private static IEnumerable<string> _Files(string folder)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A plugin UI directory is a link.");
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A plugin UI entry is a link.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in _Files(path))
                {
                    yield return child;
                }
            }
            else if (!Path.GetFileName(path).StartsWith('.'))
            {
                yield return path;
            }
        }
    }
}
