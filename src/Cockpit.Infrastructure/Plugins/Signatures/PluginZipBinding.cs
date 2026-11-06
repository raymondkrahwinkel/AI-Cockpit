using System.IO.Compression;
using System.Text;
using Cockpit.Core.Plugins;

// DEP-564 §3.5 step 6: binds the zip to the signed id and version without unpacking anything. Null when it is bound, otherwise why not.
public static class PluginZipBinding
{
    private const int MaxManifestBytes = 1024 * 1024;

    public static string? Refusal(string zipPath, string expectedId, string expectedVersion)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            // The installer extracts every entry with overwrite: the last one of a name wins, and on Windows "plugin.json." and
            // "PLUGIN.JSON" land on the same file. So anything that could become plugin.json counts; there must be exactly one, in plain form.
            var manifests = zip.Entries.Where(entry => _LandsOnManifest(entry.FullName)).ToList();
            if (manifests.Count != 1 || manifests[0].FullName != "plugin.json")
                return $"expected one plugin.json at the root, found {manifests.Count} that could land there";
            if (manifests[0].Length > MaxManifestBytes)
                return "plugin.json is too large";

            using var stream = manifests[0].Open();
            var buffer = new byte[MaxManifestBytes + 1];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read > MaxManifestBytes)
                return "plugin.json is too large";

            if (!PluginManifest.TryParse(new UTF8Encoding(false, true).GetString(buffer, 0, read), out var manifest, out var error) || manifest is null)
                return $"plugin.json does not parse: {error}";

            return manifest.Id == expectedId && manifest.Version == expectedVersion
                ? null
                : $"plugin.json says {manifest.Id} {manifest.Version}, signed was {expectedId} {expectedVersion}";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or DecoderFallbackException)
        {
            return $"unreadable zip: {exception.Message}";
        }
    }

    private static bool _LandsOnManifest(string fullName)
    {
        var path = new List<string>();
        foreach (var segment in fullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (path.Count > 0)
                    path.RemoveAt(path.Count - 1);
                continue;
            }
            path.Add(segment);
        }

        return path.Count == 1 && path[0].TrimEnd('.', ' ').Equals("plugin.json", StringComparison.OrdinalIgnoreCase);
    }
}
