using System.Reflection;

namespace Cockpit.Infrastructure.Plugins;

// AC-1013: bundled plugins carried inside a single-file executable — embedded as resources and unpacked to
// temp at startup so the ordinary installer takes over unchanged. Only used when the folder is absent; a
// normal build's folder always wins, since silently overriding a developer's edits would be hard to debug.
internal static class BundledPluginResources
{
    // Every embedded plugin file is named `bundled-plugins/&lt;id&gt;/&lt;file&gt;` — the folder layout, kept as a name.
    private const string Prefix = BundledPluginInstaller.BundledFolderName + "/";

    // Unpacks the embedded plugins and returns the directory, or null when this build has none embedded.
    public static string? TryExtract()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(BundledPluginResources).Assembly;
            var names = assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
                .ToList();

            if (names.Count == 0)
            {
                return null;
            }

            // A fresh directory per run: a stale file from an older build sitting in a reused one would be
            // installed as if it were current.
            var root = Path.Combine(Path.GetTempPath(), $"cockpit-bundled-{Guid.NewGuid():N}");

            foreach (var name in names)
            {
                var relative = name[Prefix.Length..];
                var path = Path.Combine(root, Path.Combine(relative.Split('/')));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                using var resource = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"The embedded plugin file {name} could not be read.");
                using var file = File.Create(path);
                resource.CopyTo(file);
            }

            return root;
        }
        catch (Exception)
        {
            // A cockpit that cannot unpack its bundled plugins still runs; it simply runs without them, which is
            // what an operator who removed them would get anyway.
            return null;
        }
    }
}
