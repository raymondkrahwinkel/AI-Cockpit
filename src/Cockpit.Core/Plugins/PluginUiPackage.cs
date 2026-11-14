namespace Cockpit.Core.Plugins;

public sealed record PluginUiPackage(string FolderId, PluginManifest Manifest, string Sha256, IReadOnlyList<PluginUiPackageFile> Files)
{
    public const long MaxBytes = 512L * 1024 * 1024;
    public const int MaxFiles = 8192;
}
