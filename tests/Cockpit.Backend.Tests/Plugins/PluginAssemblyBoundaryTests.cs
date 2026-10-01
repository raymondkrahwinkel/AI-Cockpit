using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Backend.Tests.Plugins;

// AC-1403, F2 acceptance 2: across every in-repo plugin, a backend part names no Avalonia and a UI part never reaches
// into its own backend part. Read from metadata, so checking loads nothing into this process.
public sealed class PluginAssemblyBoundaryTests
{
    [Fact]
    public void NoBackendPartReferencesAvalonia_AndNoUiPartItsOwnBackend()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, BundledPluginInstaller.BundledFolderName);
        var manifests = Directory.EnumerateDirectories(bundled)
            .Select(folder => (Folder: folder, Manifest: PluginManifest.TryParse(File.ReadAllText(Path.Combine(folder, "plugin.json")), out var manifest, out _) ? manifest : null))
            .Where(plugin => plugin.Manifest is not null && plugin.Manifest.Id != "ui-in-backend")
            .ToList();

        var crossings = new List<string>();
        foreach (var (folder, manifest) in manifests)
        {
            if (manifest?.EntryAssembly is { } backend
                && _References(Path.Combine(folder, backend)).Any(name => name.StartsWith("Avalonia", StringComparison.Ordinal)))
            {
                crossings.Add($"{manifest.Id}: backend part references Avalonia");
            }

            if (manifest is { EntryAssembly: { } entry, UiAssembly: { } ui } && entry != ui
                && _References(Path.Combine(folder, ui)).Contains(Path.GetFileNameWithoutExtension(entry)))
            {
                crossings.Add($"{manifest.Id}: UI part references its backend part");
            }
        }

        Assert.Equal(33, manifests.Count);

        // AC-1418 (F2.10b) splits Autopilot into two assemblies; this row goes when it lands.
        Assert.Equal(["autopilot: backend part references Avalonia"], crossings);
    }

    private static List<string> _References(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var reader = new PEReader(stream);
        var metadata = reader.GetMetadataReader();
        return [.. metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))];
    }
}
