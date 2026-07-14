using System.Reflection;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// The plugins this build ships, carried inside the executable rather than in a folder beside it.
/// <para>
/// A single-file build is one file — that is the whole promise, and a bundled-plugins directory that has to
/// travel with it is a second thing to lose. So the same files are embedded as resources and unpacked to a temp
/// directory at startup, from which the ordinary installer takes over: nothing downstream needs to know which
/// kind of build it is running in.
/// </para>
/// <para>
/// Only used when the folder is absent. A normal build keeps its folder, and the folder wins — it is what a
/// developer edits and rebuilds, and an embedded copy quietly overriding it would be a mystery to debug.
/// </para>
/// </summary>
internal static class BundledPluginResources
{
    /// <summary>Every embedded plugin file is named <c>bundled-plugins/&lt;id&gt;/&lt;file&gt;</c> — the folder layout, kept as a name.</summary>
    private const string Prefix = BundledPluginInstaller.BundledFolderName + "/";

    /// <summary>Unpacks the embedded plugins and returns the directory, or null when this build has none embedded.</summary>
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
