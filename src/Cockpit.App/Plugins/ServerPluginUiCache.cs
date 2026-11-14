using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Capabilities;

namespace Cockpit.App.Plugins;

internal static class ServerPluginUiCache
{
    internal static async Task<DiscoveredPlugin> GetAsync(IPluginAdministration administration, InstalledPlugin installed, CancellationToken cancellationToken = default)
    {
        if (administration is not IPluginUiPackages packages || installed.Registration is not { Enabled: true } registration)
        {
            throw new InvalidOperationException("The server does not offer this plugin's approved UI package.");
        }
        var version = installed.RunningVersion ?? installed.Discovered.Manifest.Version;
        var runningSha256 = installed.RunningSha256 ?? registration.PinnedSha256;
        var package = await packages.GetUiPackageAsync(installed.Discovered.FolderId, version, runningSha256, cancellationToken);
        if (package.Manifest is null || package.Files is null || package.Sha256 is null
            || package.Files.Any(file => file is null || file.Bytes is null || file.Path is null)
            || package.FolderId != installed.Discovered.FolderId || package.Manifest.Id != installed.Discovered.Manifest.Id
            || string.IsNullOrEmpty(package.FolderId) || package.FolderId != PluginFolderName.Normalize(package.FolderId)
            || package.Manifest.Version != version || !string.Equals(package.Sha256, runningSha256, StringComparison.OrdinalIgnoreCase)
            || package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit)
            || package.Files.Count is 0 or > PluginUiPackage.MaxFiles
            || package.Files.Sum(file => file.Bytes.LongLength) > PluginUiPackage.MaxBytes)
        {
            throw new PluginIntegrityException("The server plugin UI package does not match its approved identity.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in package.Files)
        {
            if (!_ValidPath(file.Path) || !paths.Add(file.Path))
            {
                throw new PluginIntegrityException("The server plugin UI package contains an invalid or duplicate path.");
            }
        }
        var hash = PluginHash.ComputeClosure(package.Files.Select(file => new PluginClosureFile(file.Path, PluginHash.Compute(file.Bytes))));
        if (!string.Equals(hash, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginIntegrityException("The server plugin UI package does not match its approved files.");
        }

        // AC-2284: this closure is a remote UI cache; no local plugin registration or backend installer sees it.
        var root = Path.Combine(CockpitBuild.StateRoot, "plugin-ui-cache");
        _CreateDirectory(root);
        var folder = Path.Combine(root, package.Sha256.ToLowerInvariant());
        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            _CreateDirectory(staging);
            foreach (var file in package.Files)
            {
                var destination = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
                _CreateDirectory(Path.GetDirectoryName(destination) ?? throw new IOException("The UI cache path has no directory."));
                await File.WriteAllBytesAsync(destination, file.Bytes, cancellationToken);
            }
            var manifestJson = await File.ReadAllTextAsync(Path.Combine(staging, "plugin.json"), cancellationToken);
            if (!PluginManifest.TryParse(manifestJson, out var manifest, out _) || manifest is null
                || manifest.Id != package.Manifest.Id || manifest.Version != version || manifest.UiAssembly is null)
            {
                throw new PluginIntegrityException("The approved plugin manifest does not offer the requested UI.");
            }
            var decision = PluginLoadPolicy.Decide(manifest, AbstractionsContract.Version, registration with { PinnedSha256 = runningSha256 }, package.Sha256,
                HostVersionInfo.Current, id => CapabilityCatalog.Find(id) is not null);
            if (decision != PluginLoadDecision.Load || !PluginEntryPath.TryResolve(staging, manifest.UiAssembly, out var uiPath) || !File.Exists(uiPath))
            {
                throw new PluginIntegrityException($"The server plugin UI is unavailable on this cockpit: {decision}.");
            }
            if (!Directory.Exists(folder))
            {
                try
                {
                    Directory.Move(staging, folder);
                }
                catch (IOException) when (Directory.Exists(folder))
                {
                    // Another window published these same approved bytes first.
                }
            }
            _NoLinks(folder);
            var discovered = new DiscoveredPlugin(folder, package.FolderId, manifest, package.Sha256, PluginLoadDecision.Load);
            PluginShadow.GetOrPrepare(discovered, fresh: true);
            return discovered;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                _NoLinks(staging);
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static bool _ValidPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 && !path.Contains('\\') && !path.Contains(':')
        && !Path.IsPathRooted(path) && !path.Any(char.IsControl)
        && path.Split('/').All(part => part.Length > 0 && part is not ("." or "..")
            && !part.EndsWith('.') && !part.EndsWith(' ') && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        && !Path.GetFileName(path).StartsWith('.');

    private static void _CreateDirectory(string folder)
    {
        Directory.CreateDirectory(folder);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(CockpitBuild.StateRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); !string.Equals(current, root, comparison); current = Path.GetDirectoryName(current)
            ?? throw new IOException("The UI cache directory is outside its state root."))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The UI cache directory is a link.");
            }
        }
    }

    private static void _NoLinks(string folder)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The UI cache directory is a link.");
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The UI cache contains a link.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                _NoLinks(path);
            }
        }
    }
}
